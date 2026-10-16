"""Validate public payload/UI; optionally install an isolated synthetic MSI fixture."""
import ctypes
import hashlib
import pathlib
import struct
import subprocess
import sys
import tempfile
import os
import shutil
import threading
import uuid
import re
from ctypes import wintypes as w

ROOT = pathlib.Path(__file__).resolve().parent
msi = ctypes.WinDLL('msi')

def api(name, args, result=w.UINT):
    fn = getattr(msi, name)
    fn.argtypes, fn.restype = args, result
    return fn

open_db = api('MsiOpenDatabaseW', [w.LPCWSTR, ctypes.c_void_p, ctypes.POINTER(w.UINT)])
open_view = api('MsiDatabaseOpenViewW', [w.UINT, w.LPCWSTR, ctypes.POINTER(w.UINT)])
execute = api('MsiViewExecute', [w.UINT, w.UINT])
fetch = api('MsiViewFetch', [w.UINT, ctypes.POINTER(w.UINT)])
field_count = api('MsiRecordGetFieldCount', [w.UINT])
get_string = api('MsiRecordGetStringW', [w.UINT, w.UINT, w.LPWSTR, ctypes.POINTER(w.DWORD)])
read_stream = api('MsiRecordReadStream', [w.UINT, w.UINT, ctypes.c_void_p, ctypes.POINTER(w.DWORD)])
close = api('MsiCloseHandle', [w.UINT])
summary_open = api('MsiGetSummaryInformationW', [w.UINT, w.LPCWSTR, w.UINT, ctypes.POINTER(w.UINT)])
summary_get = api('MsiSummaryInfoGetPropertyW', [w.UINT, w.UINT, ctypes.POINTER(w.UINT), ctypes.POINTER(ctypes.c_int), ctypes.c_void_p, w.LPWSTR, ctypes.POINTER(w.DWORD)])
open_package = api('MsiOpenPackageExW', [w.LPCWSTR, w.DWORD, ctypes.POINTER(w.UINT)])
set_property = api('MsiSetPropertyW', [w.UINT, w.LPCWSTR, w.LPCWSTR])
evaluate = api('MsiEvaluateConditionW', [w.UINT, w.LPCWSTR], ctypes.c_int)
do_action = api('MsiDoActionW', [w.UINT, w.LPCWSTR])
set_ui = api('MsiSetInternalUI', [w.UINT, ctypes.c_void_p])
enable_log = api('MsiEnableLogW', [w.DWORD, w.LPCWSTR, w.DWORD])
install_product = api('MsiInstallProductW', [w.LPCWSTR, w.LPCWSTR])
commit = api('MsiDatabaseCommit', [w.UINT])

def ok(code):
    if code:
        raise RuntimeError('Windows Installer validation error ' + str(code))

def text(record, field):
    size = w.DWORD(1048576)
    value = ctypes.create_unicode_buffer(size.value)
    ok(get_string(record, field, value, ctypes.byref(size)))
    return value.value

def query(db, sql):
    view = w.UINT()
    ok(open_view(db, sql, ctypes.byref(view)))
    try:
        ok(execute(view, 0))
        rows = []
        while True:
            record = w.UINT()
            code = fetch(view, ctypes.byref(record))
            if code == 259:
                return rows
            ok(code)
            try:
                rows.append([text(record, i) for i in range(1, field_count(record) + 1)])
            finally:
                close(record)
    finally:
        close(view)

def stream(db, name):
    view, record = w.UINT(), w.UINT()
    ok(open_view(db, "SELECT `Data` FROM `_Streams` WHERE `Name`='" + name + "'", ctypes.byref(view)))
    try:
        ok(execute(view, 0))
        ok(fetch(view, ctypes.byref(record)))
        data = bytearray()
        while True:
            buf, size = ctypes.create_string_buffer(65536), w.DWORD(65536)
            ok(read_stream(record, 1, buf, ctypes.byref(size)))
            if not size.value:
                return bytes(data)
            data += buf.raw[:size.value]
            if len(data) > 20000000:
                raise RuntimeError('Unexpected oversized public stream')
    finally:
        if record.value:
            close(record)
        close(view)

def summary(db, key):
    handle = w.UINT()
    ok(summary_open(db, None, 0, ctypes.byref(handle)))
    try:
        kind, number, size = w.UINT(), ctypes.c_int(), w.DWORD(4096)
        value, stamp = ctypes.create_unicode_buffer(4096), ctypes.create_string_buffer(8)
        ok(summary_get(handle, key, ctypes.byref(kind), ctypes.byref(number), stamp, value, ctypes.byref(size)))
        return kind.value, number.value, value.value
    finally:
        close(handle)

def digest(path):
    return hashlib.sha256(path.read_bytes()).digest()

def shortcut_target(path):
    """Read our synthetic shortcut through Windows; do not resolve or execute it."""
    ole = ctypes.OleDLL('ole32')
    ole.CoInitialize.argtypes = [ctypes.c_void_p]
    ole.CoInitialize(None)
    guid = ctypes.c_ubyte * 16
    create = ole.CoCreateInstance
    create.argtypes = [ctypes.POINTER(guid), ctypes.c_void_p, w.DWORD, ctypes.POINTER(guid), ctypes.POINTER(ctypes.c_void_p)]
    create.restype = w.LONG
    def identifier(value):
        return guid.from_buffer_copy(uuid.UUID(value).bytes_le)
    def method(pointer, index, arguments, result=w.LONG):
        table = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        return ctypes.WINFUNCTYPE(result, ctypes.c_void_p, *arguments)(table[index])
    def hresult(code):
        if code < 0:
            raise RuntimeError('Windows shortcut read failed')
    link, persisted = ctypes.c_void_p(), ctypes.c_void_p()
    try:
        clsid = identifier('00021401-0000-0000-c000-000000000046')
        iid = identifier('000214f9-0000-0000-c000-000000000046')
        hresult(create(ctypes.byref(clsid), None, 1, ctypes.byref(iid), ctypes.byref(link)))
        persist_id = identifier('0000010b-0000-0000-c000-000000000046')
        hresult(method(link, 0, [ctypes.POINTER(guid), ctypes.POINTER(ctypes.c_void_p)])(link, ctypes.byref(persist_id), ctypes.byref(persisted)))
        hresult(method(persisted, 5, [w.LPCWSTR, w.DWORD])(persisted, str(path), 0))
        value = ctypes.create_unicode_buffer(32768)
        hresult(method(link, 3, [w.LPWSTR, ctypes.c_int, ctypes.c_void_p, w.DWORD])(link, value, len(value), None, 4))
        return pathlib.Path(value.value)
    finally:
        for pointer in (persisted, link):
            if pointer.value:
                method(pointer, 2, [], w.ULONG)(pointer)
        ole.CoUninitialize()

class FixtureUi:
    """Drive only MSI dialogs in this test process; never inspect other windows."""
    def __init__(self, choice):
        self.choice, self.seen, self.errors = choice, [], []
        self.stop = threading.Event()
        self.user = ctypes.WinDLL('user32')
        self.window_callback = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
        self.user.GetWindowThreadProcessId.argtypes = [w.HWND, ctypes.POINTER(w.DWORD)]
        self.user.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
        self.user.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
        self.user.PostMessageW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM]
        self.user.EnumWindows.argtypes = [self.window_callback, w.LPARAM]
        self.user.EnumChildWindows.argtypes = [w.HWND, self.window_callback, w.LPARAM]
        self.handler_type = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_void_p, w.UINT, w.LPCWSTR)
        self.external = api('MsiSetExternalUIW', [self.handler_type, w.DWORD, ctypes.c_void_p], ctypes.c_void_p)
        @self.handler_type
        def handler(context, kind, message):
            # Suppress unexpected error boxes so a failed automated test cannot hang.
            self.errors.append(kind)
            return 2
        self.handler = handler
        self.external(self.handler, 15, None)
        self.thread = threading.Thread(target=self.run, daemon=True)

    def run(self):
        clicked = set()
        while not self.stop.wait(.1):
            @self.window_callback
            def window(hwnd, unused):
                pid = w.DWORD()
                self.user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
                if pid.value != os.getpid():
                    return True
                klass, title = ctypes.create_unicode_buffer(256), ctypes.create_unicode_buffer(256)
                self.user.GetClassNameW(hwnd, klass, 256)
                self.user.GetWindowTextW(hwnd, title, 256)
                if klass.value != 'MsiDialogCloseClass' or title.value != 'GPT Switcher Setup':
                    return True
                @self.window_callback
                def child(handle, unused):
                    name, kind = ctypes.create_unicode_buffer(256), ctypes.create_unicode_buffer(64)
                    self.user.GetWindowTextW(handle, name, 256)
                    self.user.GetClassNameW(handle, kind, 64)
                    label = name.value.replace('&', '')
                    if kind.value == 'Button' and label in (self.choice, 'Finish') and handle not in clicked:
                        clicked.add(handle)
                        self.seen.append(label)
                        self.user.PostMessageW(handle, 0xF5, 0, 0)  # BM_CLICK, test-owned control only.
                    return True
                self.user.EnumChildWindows(hwnd, child, 0)
                return True
            self.user.EnumWindows(window, 0)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *unused):
        self.stop.set()
        self.thread.join(2)
        self.external(self.handler_type(), 0, None)

def check_log(path, strict=False):
    value = path.read_text(encoding='utf-16', errors='replace')
    # The engine probes optional patch/assembly tables even in successful packages.
    # Reject missing required tables and user-facing errors, not those harmless probes.
    assert 'DEBUG: Error 2228:' not in value, 'Installer reported error 2228'
    assert '1: 2613 ' not in value, 'Installer upgrade sequencing failed (2613)'
    if strict:
        assert '1: 2228 ' not in value, 'Installer UI queried a missing table (2228)'
    assert not re.search(r'1: 2228 .*3: (?:Error|Signature|ActionText|ControlCondition|UIText) ', value)
    assert 'Return value 3' not in value, 'Installer action failed'

def ui_worker(path, log):
    # COM and a real full-UI session reproduce the font/message lookup missed by costing.
    ole = ctypes.OleDLL('ole32')
    ole.CoInitialize(None)
    ok(enable_log(0x3fff, str(log), 0))
    previous = set_ui(5, None)
    session = w.UINT()
    try:
        ok(open_package(str(path), 1, ctypes.byref(session)))
        for action, expected in [('WelcomeDlg', 1602), ('ProgressDlg', 0), ('FinishedDlg', 0), ('CancelledDlg', 0), ('FailedDlg', 0)]:
            with FixtureUi('Cancel') as driver:
                code = do_action(session, action)
                assert code == expected, 'Unexpected result from installer dialog: ' + action
                assert not driver.errors, 'Installer dialog produced an error: ' + action
                if action != 'ProgressDlg':
                    assert driver.seen, 'Installer dialog did not become usable: ' + action
    finally:
        if session.value:
            close(session)
        set_ui(previous, None)
        enable_log(0, None, 0)
        ole.CoUninitialize()
    check_log(log, strict=True)
    print('PASS full-UI welcome/cancel, progress, success, cancellation and failure dialogs; no 2228.')

def sql_literal(value):
    return "'" + str(value).replace("'", "''") + "'"

def change(db, sql):
    view = w.UINT()
    ok(open_view(db, sql, ctypes.byref(view)))
    try:
        ok(execute(view, 0))
    finally:
        close(view)

def random_guid():
    return '{' + str(uuid.uuid4()).upper() + '}'


def make_fixture(source, destination, product, upgrade, components, registry_key):
    """Keep real upgrade sequencing; replace only machine-specific test identities."""
    shutil.copyfile(source, destination)
    db = w.UINT()
    ok(open_db(str(destination), ctypes.c_void_p(1), ctypes.byref(db)))
    try:
        for key, value in [('ProductCode', product), ('UpgradeCode', upgrade), ('ProductName', 'GPT Switcher validation fixture')]:
            change(db, 'UPDATE `Property` SET `Value`=' + sql_literal(value) + ' WHERE `Property`=' + sql_literal(key))
        for row in query(db, 'SELECT `Component` FROM `Component`'):
            change(db, 'UPDATE `Component` SET `ComponentId`=' + sql_literal(components[row[0]]) + ' WHERE `Component`=' + sql_literal(row[0]))
        change(db, 'UPDATE `Registry` SET `Key`=' + sql_literal(registry_key))
        for original, synthetic, name in [('DesktopFolder', 'FixtureDesktop', 'test-desktop'), ('ProgramMenuFolder', 'FixtureMenu', 'test-menu')]:
            change(db, 'UPDATE `Shortcut` SET `Directory_`=' + sql_literal(synthetic) + ' WHERE `Directory_`=' + sql_literal(original))
            change(db, 'DELETE FROM `Directory` WHERE `Directory`=' + sql_literal(original))
            change(db, 'INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (' + sql_literal(synthetic) + ",'INSTALLFOLDER'," + sql_literal(name) + ')')
        upgrade_rows = query(db, 'SELECT `UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty` FROM `Upgrade`')
        change(db, 'DELETE FROM `Upgrade`')
        for row in upgrade_rows:
            values = [sql_literal(upgrade)] + [sql_literal(value) if value else 'NULL' for value in row[1:]]
            values[4] = str(int(row[4]))
            change(db, 'INSERT INTO `Upgrade` (`UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty`) VALUES (' + ','.join(values) + ')')
        change(db, "DELETE FROM `AppSearch` WHERE `Property`='EXISTINGSTARTUP'")
        change(db, "DELETE FROM `RegLocator` WHERE `Signature_`='ExistingStartup'")
        for action in ('ConfigureStartup', 'RollbackNewStartup', 'RemoveOwnStartup', 'StopSwitcher'):
            change(db, 'DELETE FROM `InstallExecuteSequence` WHERE `Action`=' + sql_literal(action))
        handle = w.UINT()
        ok(summary_open(db, None, 1, ctypes.byref(handle)))
        try:
            put = api('MsiSummaryInfoSetPropertyW', [w.UINT, w.UINT, w.UINT, ctypes.c_int, ctypes.c_void_p, w.LPCWSTR])
            persist = api('MsiSummaryInfoPersist', [w.UINT])
            ok(put(handle, 9, 30, 0, None, random_guid()))
            ok(persist(handle))
        finally:
            close(handle)
        ok(commit(db))
    finally:
        close(db)


def cabinet_hashes(path, folder):
    db = w.UINT()
    ok(open_db(str(path), None, ctypes.byref(db)))
    try:
        cabinet = stream(db, 'payload.cab')
    finally:
        close(db)
    folder.mkdir()
    source = folder / 'payload.cab'
    source.write_bytes(cabinet)
    extracted = folder / 'files'
    extracted.mkdir()
    result = subprocess.run(['expand.exe', '-F:*', str(source), str(extracted)], capture_output=True)
    assert result.returncode == 0, 'Previous public payload extraction failed'
    return {'ChatGPTAccountSwitcher.exe': digest(extracted / 'File001'), 'docs/USAGE.md': digest(extracted / 'File006'), 'docs/images/panel-en.png': digest(extracted / 'File011')}


def isolated_install(path, previous=None):
    """Test real fresh/upgrade/rollback/repair/remove paths with isolated products."""
    with tempfile.TemporaryDirectory(prefix='switcher-msi-fixture-') as temporary:
        folder = pathlib.Path(temporary)
        upgrade = random_guid()
        products = [random_guid(), random_guid(), random_guid()]
        components = {name: random_guid() for name in ('AppComponent', 'DocsComponent', 'ImagesComponent')}
        registry_key = 'Software\\git-buster\\GPTSwitcherValidation\\' + uuid.uuid4().hex
        current = folder / 'current.msi'
        make_fixture(path, current, products[0], upgrade, components, registry_key)
        target = folder / 'program'
        desktop, menu = target / 'test-desktop', target / 'test-menu'
        for directory in (target, desktop, menu):
            directory.mkdir(parents=True)
        marker = folder / 'fictional-private-data.txt'
        marker.write_text('Synthetic data must survive uninstall.', encoding='utf-8')
        properties = 'INSTALLFOLDER="' + str(target) + '\\" REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable'
        state = api('MsiQueryProductStateW', [w.LPCWSTR], ctypes.c_int)
        expected = {'ChatGPTAccountSwitcher.exe': digest(ROOT / 'dist/ChatGPTAccountSwitcher.exe'), 'docs/USAGE.md': digest(ROOT / 'docs/USAGE.md'), 'docs/images/panel-en.png': digest(ROOT / 'docs/images/panel-en.png')}

        def files_match(hashes):
            for relative, value in hashes.items():
                assert digest(target / relative) == value, 'Installed file differs'
            for shortcut in (desktop / 'GPT Switcher.lnk', menu / 'GPT Switcher.lnk'):
                data = shortcut.read_bytes()
                assert data[:4] == b'\x4c\0\0\0' and os.path.samefile(shortcut_target(shortcut), target / 'ChatGPTAccountSwitcher.exe'), 'Shortcut target differs'
            assert marker.read_text(encoding='utf-8') == 'Synthetic data must survive uninstall.'

        def run(package, phase, extra='', failure=False):
            log = folder / (phase + '.log')
            result = subprocess.run([sys.executable, str(pathlib.Path(__file__).resolve()), '--install-worker', str(package), str(log), properties + extra], capture_output=True, text=True, timeout=90)
            if failure:
                assert result.returncode != 0 and '1: 2613 ' in log.read_text(encoding='utf-16', errors='replace'), 'Broken ordering must reproduce 2613'
                return log
            if result.returncode:
                if log.exists():
                    diagnostic = ROOT / 'artifacts/installer-fixture-failure.log'
                    diagnostic.parent.mkdir(exist_ok=True)
                    shutil.copyfile(log, diagnostic)
                    value = log.read_text(encoding='utf-16', errors='replace')
                    print('Synthetic installer diagnostic codes:', ', '.join(re.findall(r'Note: 1: (\d+) ', value)[-20:]))
                raise RuntimeError('Isolated MSI ' + phase + ' failed: ' + result.stdout.strip()[-400:])
            print('PASS synthetic MSI ' + phase, flush=True)
            return log

        try:
            if previous:
                baseline, broken = folder / 'previous.msi', folder / 'broken-upgrade.msi'
                make_fixture(previous, baseline, products[1], upgrade, components, registry_key)
                previous_hashes = cabinet_hashes(previous, folder / 'previous-payload')
                run(baseline, 'previous-install')
                files_match(previous_hashes)
                assert state(products[1]) == 5
                make_fixture(path, broken, products[2], upgrade, components, registry_key)
                db = w.UINT()
                ok(open_db(str(broken), ctypes.c_void_p(1), ctypes.byref(db)))
                try:
                    change(db, "DELETE FROM `InstallExecuteSequence` WHERE `Action`='InstallExecute'")
                    ok(commit(db))
                finally:
                    close(db)
                run(broken, 'reproduce-2613', failure=True)
                assert state(products[1]) == 5 and state(products[2]) != 5, 'Failed upgrade must retain previous product'
                files_match(previous_hashes)
                print('PASS original sequencing reproduces 2613; previous files and registration survive rollback.', flush=True)
                log = run(current, 'upgrade')
                value = log.read_text(encoding='utf-16', errors='replace')
                assert 'RemoveExistingProducts' in value and products[1] in value
                assert state(products[1]) == -1 and state(products[0]) == 5, 'Upgrade must remove previous registration and retain current'
                files_match(expected)
                print('PASS real related-product discovery, late removal and binary update from previous public MSI.', flush=True)
            else:
                run(current, 'install')
                files_match(expected)
                assert state(products[0]) == 5
            (target / 'docs/USAGE.md').unlink()
            run(current, 'repair')
            files_match(expected)
            run(current, 'remove', ' REMOVE=ALL')
            assert state(products[0]) == -1 and not (target / 'ChatGPTAccountSwitcher.exe').exists()
            assert not list(desktop.glob('*.lnk')) and not list(menu.glob('*.lnk'))
            assert marker.read_text(encoding='utf-8') == 'Synthetic data must survive uninstall.'
        finally:
            old = set_ui(2, None)
            try:
                configure = api('MsiConfigureProductExW', [w.LPCWSTR, ctypes.c_int, ctypes.c_int, w.LPCWSTR])
                for product in products:
                    code = configure(product, 0, 2, 'REMOVE=ALL REBOOT=ReallySuppress')
                    assert code in (0, 1605), 'Could not clean up the synthetic product'
            finally:
                set_ui(old, None)
        print('PASS isolated MSI lifecycle. Upgrade discovery/removal remains enabled; random related-product/component IDs, temporary destinations and synthetic registry key; startup helpers suppressed. No account stores used.')

def install_worker(path, log, properties):
    ole = ctypes.OleDLL('ole32')
    ole.CoInitialize(None)
    ok(enable_log(0x3fff, str(log), 0))
    old = set_ui(5, None)
    try:
        with FixtureUi('Install / repair') as driver:
            ok(install_product(str(path), properties))
            assert not driver.errors, 'Windows Installer reported an error'
            assert 'Finish' in driver.seen, 'Completion dialog did not become usable'
    finally:
        set_ui(old, None)
        enable_log(0, None, 0)
        ole.CoUninitialize()
    check_log(log)
    print('PASS isolated install worker')

def validate(path):
    db = w.UINT()
    ok(open_db(str(path), None, ctypes.byref(db)))
    try:
        props = dict(query(db, 'SELECT `Property`,`Value` FROM `Property`'))
        assert props['Manufacturer'] == 'git-buster'
        assert props.get('ALLUSERS', '') == '' and props.get('STARTONLOGIN', '') == '' and props['STARTUPMODE'] == '0'
        assert summary(db, 4)[2] == 'git-buster' and summary(db, 7)[2] == 'Intel;1033'
        assert summary(db, 15)[1] & 8
        for key in (8, 12, 13):
            assert summary(db, key)[0] == 0, 'Unexpected author/time metadata'
        expected = ['dist/ChatGPTAccountSwitcher.exe', 'dist/ChatGPTAccountSwitcher.exe.config', 'README.md', 'SECURITY.md', 'LICENSE', 'docs/USAGE.md', 'docs/README.zh-Hans.md', 'docs/README.zh-Hant.md', 'docs/README.fr.md', 'docs/README.ja.md']
        expected += ['docs/images/' + name for name in ['panel-en.png', 'panel-zh-Hans.png', 'panel-zh-Hant.png', 'panel-fr.png', 'panel-ja.png', 'settings-en.png', 'app-en.png', 'sharing-en.png', 'quota-en.png', 'about-en.png', 'history-en.png', 'history-zh-Hans.png', 'history-zh-Hant.png', 'history-fr.png', 'history-ja.png']]
        rows = query(db, 'SELECT `File`,`FileName`,`FileSize`,`Sequence` FROM `File` ORDER BY `Sequence`')
        assert len(rows) == len(expected)
        for i, (row, relative) in enumerate(zip(rows, expected), 1):
            assert row == ['File' + str(i).zfill(3), pathlib.PurePosixPath(relative).name, str((ROOT / relative).stat().st_size), str(i)]
        actions = {row[0]: row[1:] for row in query(db, 'SELECT `Action`,`Type`,`Source`,`Target` FROM `CustomAction`')}
        assert actions['ConfigureStartup'] == ['1042', 'File001', '--configure-startup [STARTUPMODE]']
        assert actions['RollbackNewStartup'] == ['1298', 'File001', '--remove-startup-if-current']
        assert actions['RemoveOwnStartup'] == ['1042', 'File001', '--remove-startup-if-current']
        sequence = {row[0]: row[1:] for row in query(db, 'SELECT `Action`,`Condition`,`Sequence` FROM `InstallExecuteSequence`')}
        assert 4000 < int(sequence['ConfigureStartup'][1]) < 6600
        ordered_actions = [name for name, values in sorted(sequence.items(), key=lambda item: int(item[1][1]))]
        index = ordered_actions.index('RemoveExistingProducts')
        assert ordered_actions[index - 1:index + 2] == ['InstallExecute', 'RemoveExistingProducts', 'InstallFinalize'], 'Late product removal requires InstallExecute before it (2613)'
        assert sequence['RemoveExistingProducts'][0] == 'OLDERPRODUCTS'
        assert int(sequence['RollbackNewStartup'][1]) < int(sequence['ConfigureStartup'][1]) and sequence['RollbackNewStartup'][0] == 'NOT REMOVE AND NOT EXISTINGSTARTUP'
        assert int(sequence['RemoveOwnStartup'][1]) < 3500 and sequence['RemoveOwnStartup'][0] == 'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE'
        for table in ('InstallExecuteSequence', 'InstallUISequence'):
            repair_sequence = {row[0]: int(row[1]) for row in query(db, 'SELECT `Action`,`Sequence` FROM `' + table + '`')}
            assert repair_sequence['RepairAll'] < repair_sequence['CostInitialize'] and repair_sequence['RepairMode'] < repair_sequence['CostInitialize'], 'Repair properties must be set before costing'
        shortcuts = query(db, 'SELECT `Directory_`,`Target`,`Icon_` FROM `Shortcut`')
        assert sorted(shortcuts) == sorted([['DesktopFolder', '[#File001]', 'SwitcherIcon'], ['ProgramMenuFolder', '[#File001]', 'SwitcherIcon'], ['ProgramMenuFolder', '[SystemFolder]msiexec.exe', 'SwitcherIcon']])
        assert query(db, "SELECT `Arguments` FROM `Shortcut` WHERE `Shortcut`='UninstallShortcut'") == [['/x [ProductCode]']]
        assert actions['StopSwitcher'] == ['1042', 'File001', '--request-exit']
        assert int(sequence['StopSwitcher'][1]) < 3500 and sequence['StopSwitcher'][0] == 'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE'
        assert len(query(db, 'SELECT * FROM `RemoveFile`')) == 3
        assert query(db, 'SELECT `Property`,`Value` FROM `CheckBox`') == [['STARTONLOGIN', '1']]
        tables = {row[0] for row in query(db, 'SELECT `Name` FROM `_Tables`')}
        assert {'Error', 'ActionText', 'UIText', 'ControlCondition', 'Signature'} <= tables, 'Required installer UI/search table is missing'
        ui_sequence = {row[0]: row[1] for row in query(db, 'SELECT `Action`,`Sequence` FROM `InstallUISequence`')}
        assert ui_sequence['FinishedDlg'] == '-1' and ui_sequence['CancelledDlg'] == '-2' and ui_sequence['FailedDlg'] == '-3'
        cabinet = stream(db, 'payload.cab')
        assert cabinet[:4] == b'MSCF' and struct.unpack_from('<H', cabinet, 30)[0] == 0
        pos = struct.unpack_from('<I', cabinet, 16)[0]
        count = struct.unpack_from('<H', cabinet, 28)[0]
        assert count == len(expected)
        names = []
        for _ in range(count):
            date, clock = struct.unpack_from('<HH', cabinet, pos + 10)
            assert date == ((2000 - 1980) << 9 | 1 << 5 | 1) and clock == 0
            end = cabinet.index(0, pos + 16)
            names.append(cabinet[pos + 16:end].decode('ascii'))
            pos = end + 1
        assert names == [row[0] for row in rows], 'Unexpected cabinet paths or entries'
        with tempfile.TemporaryDirectory(prefix='switcher-public-msi-') as temporary:
            folder = pathlib.Path(temporary)
            source, target = folder / 'payload.cab', folder / 'payload'
            source.write_bytes(cabinet)
            target.mkdir()
            result = subprocess.run(['expand.exe', '-F:*', str(source), str(target)], capture_output=True)
            assert result.returncode == 0, 'Public cabinet extraction failed (exit ' + str(result.returncode) + ')'
            assert sorted(p.name for p in target.iterdir()) == sorted(names)
            for row, relative in zip(rows, expected):
                assert digest(target / row[0]) == digest(ROOT / relative)
            # Only evaluate conditions and costing. Never execute install/uninstall actions.
            session = w.UINT()
            previous_ui = set_ui(2, None)
            try:
                ok(open_package(str(path), 1, ctypes.byref(session)))
                condition = 'Installed OR (NETFRAMEWORK48 >= "#528040")'
                ok(set_property(session, 'NETFRAMEWORK48', '#528040'))
                assert evaluate(session, condition) == 1
                ok(set_property(session, 'NETFRAMEWORK48', '#528039'))
                assert evaluate(session, condition) == 0
                ok(set_property(session, 'INSTALLFOLDER', str(folder / 'fictional-install') + '\\'))
                for action in ('CostInitialize', 'FileCost', 'CostFinalize'):
                    ok(do_action(session, action))
            finally:
                if session.value:
                    close(session)
                set_ui(previous_ui, None)
            result = subprocess.run([sys.executable, str(pathlib.Path(__file__).resolve()), '--ui-worker', str(path), str(folder / 'ui.log')], capture_output=True, text=True, timeout=45)
            if result.returncode:
                raise RuntimeError(result.stdout.strip()[-500:])
            print(result.stdout.strip())
    finally:
        close(db)
    print('PASS MSI defaults, shortcuts, per-user scope, startup/uninstall actions, metadata, exact 25-file cabinet hashes and read-only Windows Installer costing. No installation or account-store access.')

if __name__ == '__main__':
    try:
        if sys.argv[1] == '--ui-worker':
            ui_worker(pathlib.Path(sys.argv[2]).resolve(), pathlib.Path(sys.argv[3]).resolve())
        elif sys.argv[1] == '--install-worker':
            install_worker(pathlib.Path(sys.argv[2]).resolve(), pathlib.Path(sys.argv[3]).resolve(), sys.argv[4])
        else:
            path = pathlib.Path(sys.argv[1]).resolve()
            validate(path)
            if '--isolated-install' in sys.argv[2:]:
                isolated_install(path, pathlib.Path(sys.argv[sys.argv.index('--upgrade-from') + 1]).resolve() if '--upgrade-from' in sys.argv else None)
    except Exception as error:
        print('FAIL installer validation:', type(error).__name__, str(error))
        sys.exit(1)
