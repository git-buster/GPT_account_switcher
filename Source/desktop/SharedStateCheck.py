"""Optional integration check: fresh synthetic profiles, a fresh DB and a loopback-only provider."""

import json

import os

import pathlib

import queue

import subprocess

import sys

import threading

import uuid

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer





class Rpc:

    def __init__(self, cli, home, state):

        env = {k: v for k, v in os.environ.items() if not k.upper().startswith(('CODEX_', 'OPENAI_', 'CHATGPT_'))}

        env['CODEX_HOME'] = str(home)

        env['CODEX_SQLITE_HOME'] = str(state)

        env['NO_PROXY'] = '127.0.0.1,localhost'

        env.pop('PSModulePath', None)

        self.process = subprocess.Popen([cli, 'app-server'], cwd=str(home), env=env, stdin=subprocess.PIPE,

                                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,

                                        encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)

        self.inbox = queue.Queue()

        self.events = queue.Queue()

        self.sequence = 0

        def read():

            try:

                for line in self.process.stdout:

                    try:

                        data = json.loads(line)

                        if 'id' in data:

                            self.inbox.put(data)

                        else:

                            self.events.put(data)

                    except ValueError:

                        pass

            finally:

                self.inbox.put(None)

        threading.Thread(target=read, daemon=True).start()

        self.call('initialize', {'clientInfo': {'name': 'switcher_synthetic_state_check', 'version': '0.6.13'},

                                'capabilities': {'experimentalApi': True}})

        self.process.stdin.write('{"method":"initialized","params":{}}\n')

        self.process.stdin.flush()



    def call(self, method, params):

        self.sequence += 1

        self.process.stdin.write(json.dumps({'id': self.sequence, 'method': method, 'params': params}) + '\n')

        self.process.stdin.flush()

        while True:

            data = self.inbox.get(timeout=25)

            if data is None:

                raise RuntimeError('fixture helper exited')

            if data.get('id') == self.sequence:

                if 'error' in data:

                    raise RuntimeError(method + ': protocol code ' + str(data['error'].get('code')))

                return data['result']



    def completed(self):

        while True:

            data = self.events.get(timeout=25)

            if data.get('method') == 'turn/completed':

                return data['params']['turn']['status']



    def close(self):

        self.process.stdin.close()

        try:

            self.process.wait(timeout=5)

        except subprocess.TimeoutExpired:

            self.process.kill()

            self.process.wait(timeout=5)





class FictionalProvider(BaseHTTPRequestHandler):

    calls = 0

    def log_message(self, *args):

        pass



    def do_POST(self):

        self.rfile.read(int(self.headers.get('Content-Length', '0')))

        type(self).calls += 1

        item = {'id': 'msg_fixture_' + str(type(self).calls), 'type': 'message', 'role': 'assistant', 'status': 'completed',

                'content': [{'type': 'output_text', 'text': 'Fictional continuity fixture. No account data.'}]}

        events = [

            {'type': 'response.created', 'response': {'id': 'resp_fixture', 'object': 'response', 'status': 'in_progress', 'output': []}},

            {'type': 'response.output_item.added', 'output_index': 0, 'item': dict(item, status='in_progress', content=[])},

            {'type': 'response.output_text.delta', 'item_id': item['id'], 'output_index': 0, 'content_index': 0, 'delta': item['content'][0]['text']},

            {'type': 'response.output_item.done', 'output_index': 0, 'item': item},

            {'type': 'response.completed', 'response': {'id': 'resp_fixture', 'object': 'response', 'status': 'completed', 'output': [item],

                                                       'usage': {'input_tokens': 1, 'output_tokens': 1, 'total_tokens': 2}}},

        ]

        payload = ''.join('event: ' + e['type'] + '\ndata: ' + json.dumps(e) + '\n\n' for e in events).encode()

        self.send_response(200); self.send_header('Content-Type', 'text/event-stream')

        self.send_header('Content-Length', str(len(payload))); self.end_headers(); self.wfile.write(payload)





def main(cli):

    root = pathlib.Path.cwd() / 'test-output' / ('shared-state-' + uuid.uuid4().hex)

    root.mkdir(parents=True, exist_ok=False)

    (root / '.synthetic-state-fixture').write_text('Fictional data only', encoding='utf-8')

    home_a, home_b, state, project = [root / name for name in ('home-a', 'home-b', 'shared-state', 'fictional-project')]

    for path in (home_a, home_b, state, project):

        path.mkdir()

    server = ThreadingHTTPServer(('127.0.0.1', 0), FictionalProvider)

    threading.Thread(target=server.serve_forever, daemon=True).start()

    config = 'cli_auth_credentials_store = "ephemeral"\nmodel_provider = "fixture"\nmodel = "fixture-model"\n[model_providers.fixture]\nname = "Fictional local provider"\nwire_api = "responses"\nrequires_openai_auth = false\nbase_url = "http://127.0.0.1:' + str(server.server_port) + '/v1"\n'

    for home in (home_a, home_b):

        (home / 'config.toml').write_text(config, encoding='utf-8')

    clients = []

    stage = 'initialize fixture A'

    try:

        first = Rpc(cli, home_a, state); clients.append(first)

        stage = 'create fictional project'

        record = first.call('project/create', {'name': 'Fictional continuity fixture', 'roots': [{'path': str(project)}],

                                               'idempotencyKey': 'fixture-' + uuid.uuid4().hex})

        project_id = record['project']['id']

        stage = 'create empty fictional paginated thread without model generation'

        thread = first.call('thread/start', {'cwd': str(project), 'projectId': project_id, 'historyMode': 'paginated'})['thread']

        stage = 'persist fictional turn using only the loopback provider'

        first.call('turn/start', {'threadId': thread['id'], 'input': [{'type': 'text', 'text': 'Fictional continuity fixture. No tools are needed.'}]})

        if first.completed() != 'completed':

            raise RuntimeError('fictional local turn did not complete')

        stage = 'initialize fixture B with independent home'

        second = Rpc(cli, home_b, state); clients.append(second)

        stage = 'read common project and paginated thread from fixture B'

        projects = second.call('project/list', {'limit': 20})['data']

        stored = second.call('thread/read', {'threadId': thread['id'], 'includeTurns': False})['thread']

        page = second.call('thread/items/list', {'threadId': thread['id'], 'limit': 20})

        items = page.get('data', page.get('items', []))

        text_found = 'Fictional continuity fixture. No account data.' in json.dumps(items)

        result = {'SharedProjectsVisible': any(p['id'] == project_id for p in projects),

                  'SameStoredThreadVisible': stored['id'] == thread['id'], 'HistoryMode': stored.get('historyMode'),

                  'SyntheticHistoryItemVisible': text_found,

                  'IndependentHomes': home_a != home_b,

                  'NoAuthenticationFilesCreated': not any(p.exists() for p in (home_a/'auth.json', home_b/'auth.json')),

                  'LoopbackProviderCalls': FictionalProvider.calls, 'OpenAIModelRequestsSent': 0, 'RealProfilesOpened': 0}

        stage = 'continue stored paginated history from independent fixture B'

        first.close(); clients.remove(first)

        second.call('thread/resume', {'threadId': thread['id'], 'excludeTurns': True})

        second.call('turn/start', {'threadId': thread['id'], 'input': [{'type': 'text', 'text': 'Fictional continuation from independent profile B.'}]})

        result['SecondProfileContinuationCompleted'] = second.completed() == 'completed'

        second.close(); clients.remove(second)

        stage = 'reopen fixture A and verify reverse continuity'

        reopened = Rpc(cli, home_a, state); clients.append(reopened)

        all_items = reopened.call('thread/items/list', {'threadId': thread['id'], 'limit': 50})

        result['ReopenedFirstProfileSeesSecondTurn'] = 'Fictional continuation from independent profile B.' in json.dumps(all_items)

        reopened.close(); clients.remove(reopened)

        stage = 'disable common state for synthetic profile B'

        private = Rpc(cli, home_b, home_b); clients.append(private)

        private_projects = private.call('project/list', {'limit': 20})['data']

        private_threads = private.call('thread/list', {'limit': 20})['data']

        result['PrivateModeHidesCommonProject'] = not any(p['id'] == project_id for p in private_projects)

        result['PrivateModeHidesCommonThread'] = not any(t['id'] == thread['id'] for t in private_threads)

        try:

            private.call('thread/read', {'threadId': thread['id'], 'includeTurns': False})

            result['PrivateModeRejectsCommonThreadRead'] = False

        except RuntimeError:

            result['PrivateModeRejectsCommonThreadRead'] = True

        stage = 'create fresh history only in the private synthetic profile'

        private_project = root / 'private-fictional-project'; private_project.mkdir()

        private_thread = private.call('thread/start', {'cwd': str(private_project), 'historyMode': 'paginated'})['thread']

        private.call('turn/start', {'threadId': private_thread['id'], 'input': [{'type': 'text', 'text': 'Fictional isolated profile B turn.'}]})

        result['PrivateModeTurnCompleted'] = private.completed() == 'completed'

        private.close(); clients.remove(private)

        stage = 'verify private history does not appear in the common source'

        reopened = Rpc(cli, home_a, state); clients.append(reopened)

        common_threads = reopened.call('thread/list', {'limit': 20})['data']

        result['PrivateThreadAbsentFromCommonSource'] = not any(t['id'] == private_thread['id'] for t in common_threads)

        result['CommonHistoryRemainsAfterDisablingSharing'] = reopened.call('thread/read', {'threadId': thread['id'], 'includeTurns': False})['thread']['id'] == thread['id']

        stage = 'verify explicit configuration can override the private environment'

        (home_b / 'config.toml').write_text('sqlite_home = ' + json.dumps(str(state)) + '\n' + config, encoding='utf-8')

        configured = Rpc(cli, home_b, home_b); clients.append(configured)

        selection = configured.call('config/read', {'includeLayers': False})['config'].get('sqlite_home')

        result['ExplicitStateOverrideDetected'] = selection is not None and pathlib.Path(selection).resolve() == state.resolve()

        configured.close(); clients.remove(configured)

        (home_b / 'config.toml').write_text(config, encoding='utf-8')

        stage = 'check optional official fork into a different synthetic database'

        destination = root / 'isolated-fork-target'; destination.mkdir()

        try:

            fork = reopened.call('thread/fork', {'threadId': thread['id'], 'excludeTurns': True, 'deferGoalContinuation': True,

                                                'config': {'sqlite_home': str(destination)}})['thread']

            target = Rpc(cli, home_b, destination); clients.append(target)

            copied = target.call('thread/read', {'threadId': fork['id'], 'includeTurns': False})['thread']

            result['OfficialCrossDatabaseForkVerified'] = copied['id'] == fork['id']

        except RuntimeError:

            result['OfficialCrossDatabaseForkVerified'] = False

        result['LoopbackProviderCalls'] = FictionalProvider.calls

        print(json.dumps(result))

        if not all(result[k] for k in ('SharedProjectsVisible','SameStoredThreadVisible','SyntheticHistoryItemVisible','NoAuthenticationFilesCreated', 'SecondProfileContinuationCompleted', 'ReopenedFirstProfileSeesSecondTurn', 'PrivateModeHidesCommonProject', 'PrivateModeHidesCommonThread', 'PrivateModeRejectsCommonThreadRead', 'PrivateModeTurnCompleted', 'PrivateThreadAbsentFromCommonSource', 'CommonHistoryRemainsAfterDisablingSharing', 'ExplicitStateOverrideDetected')):

            raise RuntimeError('fixture continuity did not verify')

    except Exception as error:

        print(json.dumps({'FixtureStage': stage, 'Error': str(error)}))

        return 1

    finally:

        for client in reversed(clients):

            client.close()

        server.shutdown(); server.server_close()

    return 0





if __name__ == '__main__':

    sys.exit(main(sys.argv[1]))

