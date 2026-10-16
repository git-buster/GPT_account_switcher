using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace DesktopAccounts {
    public sealed class HistorySetupRequiredException : IOException { }
    public sealed class HistoryConflictException : IOException { }
    public static class HistoryJunction {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool RemoveDirectory(string path);
        public static string Target(string path) {
            Safe.PlainPath(Path.GetDirectoryName(path));
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; }
            if ((attributes & FileAttributes.ReparsePoint) == 0) return null;
            using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new Win32Exception();
                var data = new byte[16384]; int length;
                if (!DeviceIoControl(handle, 0x900A8, null, 0, data, data.Length, out length, IntPtr.Zero) || length < 16 || BitConverter.ToUInt32(data, 0) != 0xA0000003) throw new IOException("Unsupported history link.");
                int offset = BitConverter.ToUInt16(data, 8), size = BitConverter.ToUInt16(data, 10);
                if (size == 0 || size % 2 != 0 || 16 + offset + size > length) throw new IOException("Invalid history link.");
                string target = Encoding.Unicode.GetString(data, 16 + offset, size);
                if (!target.StartsWith(@"\??\", StringComparison.Ordinal)) throw new IOException("Nonlocal history link.");
                return Path.GetFullPath(target.Substring(4)).TrimEnd('\\');
            }
        }
        public static bool Matches(string path, string expected) { return String.Equals(Target(path), Path.GetFullPath(expected).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        public static void Create(string path, string target) {
            Safe.PlainPath(Path.GetDirectoryName(path)); Safe.PlainPath(target);
            if (!Directory.Exists(target) || Directory.Exists(path) || File.Exists(path)) throw new IOException("History link location is occupied.");
            var first = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target).TrimEnd('\\'));
            var second = Encoding.Unicode.GetBytes(Path.GetFullPath(target).TrimEnd('\\'));
            var data = new byte[16 + first.Length + second.Length + 4];
            Array.Copy(BitConverter.GetBytes(0xA0000003u), 0, data, 0, 4); Array.Copy(BitConverter.GetBytes((ushort)(data.Length - 8)), 0, data, 4, 2);
            Array.Copy(BitConverter.GetBytes((ushort)first.Length), 0, data, 10, 2); Array.Copy(BitConverter.GetBytes((ushort)(first.Length + 2)), 0, data, 12, 2); Array.Copy(BitConverter.GetBytes((ushort)second.Length), 0, data, 14, 2);
            Array.Copy(first, 0, data, 16, first.Length); Array.Copy(second, 0, data, 18 + first.Length, second.Length);
            Directory.CreateDirectory(path);
            try {
                using (var handle = CreateFile(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)) {
                    int returned;
                    if (handle.IsInvalid || !DeviceIoControl(handle, 0x900A4, data, data.Length, null, 0, out returned, IntPtr.Zero)) throw new Win32Exception();
                }
                if (!Matches(path, target)) throw new IOException("History link verification failed.");
            } catch { if (Directory.Exists(path)) RemoveDirectory(path); throw; }
        }
        public static void Remove(string path, string target) { Safe.PlainPath(Path.GetDirectoryName(path)); if (!Matches(path, target) || !RemoveDirectory(path)) throw new IOException("History link changed; left intact."); }
    }
    public sealed class HistoryMove { public string From, To; }
    public sealed class HistoryLink { public string Path, Target; }
    public sealed class HistoryJournal {
        public string Id, Stage, Mode, CanonicalHome, StateHome, BackupDirectory;
        public bool IndexStarted, OldShared, OldProjects; public int OldLayout, OldVersion; public string OldStateHome;
        public List<HistoryMove> Moves = new List<HistoryMove>();
        public List<HistoryLink> Links = new List<HistoryLink>();
        public List<string> Databases = new List<string>();
        public int PersistedMoves, PersistedLinks;
    }
    public sealed class HistoryAction { public HistoryMove Move; public HistoryLink Link; }
    public sealed class HistoryOrganizationResult { public int MovedFiles, ImportedThreads, VerifiedThreads, Accounts; public string BackupDirectory; }
    public sealed class HistoryIndexSession : IDisposable {
        readonly OfficialInstall official; LocalSession session;
        public HistoryIndexSession(OfficialInstall install) { official = install; }
        public int ProcessId { get { return session == null ? 0 : session.ProcessId; } }
        public bool Check(string home, string db, string file, bool archived) {
            if (session == null) session = new LocalSession(official, home, db);
            string id = Regex.Match(Path.GetFileName(file), @"([0-9a-fA-F-]{36})\.jsonl$").Groups[1].Value;
            object thread = null;
            try { thread = Safe.Field(session.HistoryCall("thread/read", new { threadId = id, includeTurns = false }), "thread"); } catch (LocalRpcException error) { if (error.Code != -32600 && error.Code != -32000) throw; }
            bool imported = thread == null;
            if (imported) {
                thread = Safe.Field(session.HistoryCall("thread/resume", new { threadId = id, path = file, excludeTurns = true }), "thread");
                if ((Safe.Field(thread, "id") as string) != id) throw new IOException("Recovered thread identity did not match.");
                if (archived) session.HistoryCall("thread/archive", new { threadId = id });
            }
            if ((Safe.Field(thread, "id") as string) != id) throw new IOException("Invalid history identity.");
            try { session.HistoryCall("thread/turns/list", new { threadId = id, limit = 1, itemsView = "notLoaded" }); } catch (LocalRpcException error) { if (error.Code != -32601) throw; }
            return imported;
        }
        public void Dispose() { if (session != null) { session.Dispose(); session = null; } }
    }
    public static class CompleteHistorySharing {
        static readonly string[] Leaves = { "sessions", "archived_sessions", "thread-writer-locks" };
        static string Canonical(PrivateStore store) { return Path.GetFullPath(store.HomeFor(new Profile { Id = "Default" })).TrimEnd('\\'); }
        static string JournalFile(PrivateStore store) { return Path.Combine(store.Root, "history-operation.dat"); }
        static void Save(PrivateStore store, HistoryJournal journal) {
            string actions = Path.Combine(journal.BackupDirectory, "actions.dat"); Safe.PlainPath(actions);
            using (var stream = new FileStream(actions, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new BinaryWriter(stream)) {
                Action<HistoryAction> append = action => {
                    byte[] plain = Encoding.UTF8.GetBytes(Safe.Json().Serialize(action));
                    try { byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(plain, null, System.Security.Cryptography.DataProtectionScope.CurrentUser); writer.Write(encrypted.Length); writer.Write(encrypted); }
                    finally { Array.Clear(plain, 0, plain.Length); }
                };
                while (journal.PersistedMoves < journal.Moves.Count) { append(new HistoryAction { Move = journal.Moves[journal.PersistedMoves] }); journal.PersistedMoves++; }
                while (journal.PersistedLinks < journal.Links.Count) { append(new HistoryAction { Link = journal.Links[journal.PersistedLinks] }); journal.PersistedLinks++; }
                writer.Flush(); stream.Flush(true);
            }
            var moves = journal.Moves; var links = journal.Links;
            try { journal.Moves = new List<HistoryMove>(); journal.Links = new List<HistoryLink>(); store.EncodeHistoryJournal(JournalFile(store), journal); }
            finally { journal.Moves = moves; journal.Links = links; }
        }
        static HistoryJournal ReadJournal(PrivateStore store) {
            var journal = store.DecodeHistoryJournal<HistoryJournal>(JournalFile(store));
            if (journal == null || !Regex.IsMatch(journal.Id ?? "", @"\A[0-9a-f]{32}\z") || journal.BackupDirectory != Path.Combine(store.Root, "HistoryBackups", journal.Id)) throw new IOException("Invalid recovery journal.");
            string path = Path.Combine(journal.BackupDirectory, "actions.dat"); Safe.PlainPath(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream)) while (stream.Position < stream.Length) {
                if (stream.Length - stream.Position < 4) break;
                int size = reader.ReadInt32(); if (size < 1 || size > 1048576) throw new IOException("Invalid recovery action.");
                if (stream.Length - stream.Position < size) break;
                byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(reader.ReadBytes(size), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                try { var action = Safe.Json().Deserialize<HistoryAction>(Encoding.UTF8.GetString(plain)); if (action == null || action.Move == null && action.Link == null) throw new IOException(); if (action.Move != null) journal.Moves.Add(action.Move); if (action.Link != null) journal.Links.Add(action.Link); }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            journal.PersistedMoves = journal.Moves.Count; journal.PersistedLinks = journal.Links.Count; return journal;
        }
        public static bool Pending(PrivateStore store) {
            if (!File.Exists(JournalFile(store))) return false;
            try { var journal = store.DecodeHistoryJournal<HistoryJournal>(JournalFile(store)); return journal == null || journal.Stage != "complete"; }
            catch { return true; }
        }
        static void MarkBackup(HistoryJournal journal) { File.WriteAllText(Path.Combine(journal.BackupDirectory, ".switcher-history-backup"), "GPT Switcher history backup " + journal.Id, new UTF8Encoding(false)); }
        static void PruneBackups(PrivateStore store, HistoryJournal current) {
            string root = Path.GetFullPath(Path.Combine(store.Root, "HistoryBackups")).TrimEnd('\\'); Safe.PlainPath(root);
            foreach (string directory in Directory.EnumerateDirectories(root)) {
                string id = Path.GetFileName(directory);
                if (id == current.Id || !Regex.IsMatch(id, @"\A[0-9a-f]{32}\z")) continue;
                string resolved = Path.GetFullPath(directory);
                if (!resolved.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException();
                PlainTree(resolved); string marker = Path.Combine(resolved, ".switcher-history-backup");
                if (!File.Exists(marker) || File.ReadAllText(marker) != "GPT Switcher history backup " + id) continue;
                Directory.Delete(resolved, true);
            }
        }
        static void PlainTree(string root) {
            Safe.PlainPath(root); if (!Directory.Exists(root)) return;
            foreach (string child in Directory.EnumerateFileSystemEntries(root)) {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected link inside history.");
                if (Directory.Exists(child)) PlainTree(child);
            }
        }
        public static void Validate(PrivateStore store, Settings settings, Profile profile) {
            if (!settings.ShareLocalState) return;
            if (settings.LocalHistoryLayout != 1) throw new HistorySetupRequiredException();
            if (Pending(store)) throw new HistorySetupRequiredException();
            string canonical = Canonical(store); Safe.PlainPath(canonical);
            foreach (string leaf in Leaves) {
                string source = Path.Combine(canonical, leaf); Safe.PlainPath(source);
                if (!Directory.Exists(source)) throw new HistorySetupRequiredException();
                if (profile.Id != "Default" && !HistoryJunction.Matches(Path.Combine(store.HomeFor(profile), leaf), source)) throw new HistorySetupRequiredException();
            }
        }
        // New empty profiles attach without copying history. Existing local records require offline organization.
        public static void PrepareNewProfile(PrivateStore store, Settings settings, Profile profile, Func<bool> targetClosed) {
            if (!settings.ShareLocalState || settings.LocalHistoryLayout != 1 || profile.Id == "Default") return;
            if (Pending(store)) throw new HistorySetupRequiredException();
            store.Prepare(profile); AttachEmptyHome(store.HomeFor(profile), Canonical(store), targetClosed);
        }
        public static void AttachEmptyHome(string home, string canonical, Func<bool> targetClosed) {
            Safe.PlainPath(home); Safe.PlainPath(canonical);
            var missing = new List<HistoryLink>();
            foreach (string leaf in Leaves) {
                string target = Path.Combine(canonical, leaf), path = Path.Combine(home, leaf); Safe.PlainPath(target);
                if (HistoryJunction.Matches(path, target)) continue;
                if (File.Exists(path)) throw new HistorySetupRequiredException();
                if (Directory.Exists(path)) {
                    PlainTree(path); var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();
                    if (files.Any(file => leaf != "thread-writer-locks" || Path.GetFileName(file) != ".coordination.lock" || Path.GetDirectoryName(file) != path || new FileInfo(file).Length != 0)) throw new HistorySetupRequiredException();
                }
                missing.Add(new HistoryLink { Path = path, Target = target });
            }
            if (missing.Count == 0) return;
            if (!targetClosed()) throw new HistorySetupRequiredException();
            var created = new List<HistoryLink>(); var retired = new List<HistoryMove>();
            try { foreach (var link in missing) {
                if (!targetClosed()) throw new HistorySetupRequiredException();
                if (Directory.Exists(link.Path)) { string old = link.Path + ".switcher-empty-" + Guid.NewGuid().ToString("N"); Directory.Move(link.Path, old); retired.Add(new HistoryMove { From = link.Path, To = old }); }
                HistoryJunction.Create(link.Path, link.Target); created.Add(link);
            } }
            catch { foreach (var link in created.AsEnumerable().Reverse()) HistoryJunction.Remove(link.Path, link.Target); foreach (var move in retired.AsEnumerable().Reverse()) Directory.Move(move.To, move.From); throw; }
        }
        static bool Prefix(string small, string large) {
            using (var left = new FileStream(small, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var right = new FileStream(large, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (left.Length > right.Length) return false;
                byte[] a = new byte[65536], b = new byte[65536]; int count;
                while ((count = left.Read(a, 0, a.Length)) != 0) {
                    int filled = 0; while (filled < count) { int size = right.Read(b, filled, count - filled); if (size == 0) return false; filled += size; }
                    for (int i = 0; i < count; i++) if (a[i] != b[i]) return false;
                }
                return true;
            }
        }
        static void Move(PrivateStore store, HistoryJournal journal, string source, string target) {
            Safe.PlainPath(source); Safe.PlainPath(target); Directory.CreateDirectory(Path.GetDirectoryName(target));
            journal.Moves.Add(new HistoryMove { From = source, To = target }); Save(store, journal);
            File.Move(source, target);
        }
        static void Merge(PrivateStore store, HistoryJournal journal, string home, string source, string target, Action<string> progress) {
            PlainTree(source); Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray()) {
                string relative = file.Substring(source.Length).TrimStart('\\'), destination = Path.Combine(target, relative);
                if (File.Exists(destination)) {
                    bool equal = new FileInfo(file).Length == new FileInfo(destination).Length && Prefix(file, destination);
                    bool extends = !equal && Path.GetExtension(file).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) && Prefix(destination, file);
                    bool older = !equal && !extends && Path.GetExtension(file).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) && Prefix(file, destination);
                    if (!equal && !extends && !older) throw new HistoryConflictException();
                    string retained = Path.Combine(journal.BackupDirectory, "retained", Guid.NewGuid().ToString("N"));
                    if (extends) Move(store, journal, destination, retained); else { Move(store, journal, file, retained); continue; }
                }
                Move(store, journal, file, destination); progress("historyMoving");
            }
        }
        static bool StateFile(string path) {
            string name = Path.GetFileName(path);
            return Regex.IsMatch(name, @"\A(?:state|thread_history|goals|memories|queue)_\d+\.sqlite(?:-wal|-shm)?\z", RegexOptions.CultureInvariant);
        }
        static void BackupDatabases(PrivateStore store, HistoryJournal journal) {
            string backup = Path.Combine(journal.BackupDirectory, "database"); Directory.CreateDirectory(backup);
            foreach (string file in Directory.EnumerateFiles(journal.StateHome).Where(StateFile)) {
                Safe.PlainPath(file); string name = Path.GetFileName(file);
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(Path.Combine(backup, name), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
                journal.Databases.Add(name);
            }
            journal.Stage = "prepared"; Save(store, journal);
        }
        static string ThreadId(string file) {
            var match = Regex.Match(Path.GetFileName(file), @"\Arollout-.+-([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.jsonl\z");
            return match.Success ? match.Groups[1].Value : null;
        }
        public static HistoryOrganizationResult Organize(PrivateStore store, Settings settings, string canonical, string state, Func<bool> allClosed, Action<string> progress, Func<string, string, string, bool, bool> index, Action finishIndex = null) {
            if (Pending(store)) throw new HistorySetupRequiredException();
            canonical = Path.GetFullPath(canonical).TrimEnd('\\'); state = Path.GetFullPath(state).TrimEnd('\\');
            Safe.PlainPath(canonical); Safe.PlainPath(state);
            if (!Directory.Exists(canonical) || !Directory.Exists(state) || !allClosed()) throw new IOException("Close official clients first.");
            var journal = new HistoryJournal { Id = Guid.NewGuid().ToString("N"), Stage = "planning", Mode = "share", CanonicalHome = canonical, StateHome = state, OldShared = settings.ShareLocalState, OldProjects = settings.ShareProjects, OldLayout = settings.LocalHistoryLayout, OldStateHome = settings.SharedStateDirectory, OldVersion = settings.Version };
            journal.BackupDirectory = Path.Combine(store.Root, "HistoryBackups", journal.Id); Safe.PlainPath(journal.BackupDirectory);
            Directory.CreateDirectory(journal.BackupDirectory);
            MarkBackup(journal);
            var result = new HistoryOrganizationResult { Accounts = settings.Accounts.Count, BackupDirectory = journal.BackupDirectory };
            try {
                progress("historyBackingUp"); BackupDatabases(store, journal);
                foreach (string leaf in Leaves) {
                    string destination = Path.Combine(canonical, leaf); PlainTree(destination); Directory.CreateDirectory(destination);
                    foreach (var profile in settings.Accounts.Where(p => p.Id != "Default")) {
                        string home = store.HomeFor(profile), source = Path.Combine(home, leaf);
                        if (HistoryJunction.Matches(source, destination)) continue;
                        if (!allClosed()) throw new IOException("Client started during organization.");
                        if (HistoryJunction.Target(source) != null) throw new IOException("Unexpected history link.");
                        store.Prepare(profile);
                        if (leaf == "thread-writer-locks") {
                            // Closed-client locks are retained separately; never combine live lock state.
                            if (Directory.Exists(source)) { PlainTree(source); string retained = Path.Combine(journal.BackupDirectory, "locks", profile.Id); Directory.CreateDirectory(retained); foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) Move(store, journal, file, Path.Combine(retained, file.Substring(source.Length).TrimStart('\\'))); }
                        } else if (Directory.Exists(source)) Merge(store, journal, home, source, destination, progress);
                        if (Directory.Exists(source)) {
                            // Move the empty directory tree rather than recursively deleting it.
                            string empty = Path.Combine(journal.BackupDirectory, "empty", profile.Id, leaf); Directory.CreateDirectory(Path.GetDirectoryName(empty));
                            journal.Moves.Add(new HistoryMove { From = source, To = empty }); Save(store, journal); Directory.Move(source, empty);
                        }
                        var link = new HistoryLink { Path = source, Target = destination }; journal.Links.Add(link); Save(store, journal); HistoryJunction.Create(source, destination);
                    }
                }
                journal.Stage = "indexing"; journal.IndexStarted = true; Save(store, journal); progress("historyIndexing");
                var histories = new[] { "sessions", "archived_sessions" }.SelectMany(leaf => Directory.EnumerateFiles(Path.Combine(canonical, leaf), "*.jsonl", SearchOption.AllDirectories)).Select(file => new { File = file, Id = ThreadId(file) }).Where(record => record.Id != null).ToArray();
                if (histories.GroupBy(record => record.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) throw new HistoryConflictException();
                foreach (string leaf in new[] { "sessions", "archived_sessions" }) foreach (string file in Directory.EnumerateFiles(Path.Combine(canonical, leaf), "*.jsonl", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToArray()) {
                    string id = ThreadId(file); if (id == null) continue;
                    if (!allClosed()) throw new IOException("Client started during organization.");
                    if (index(canonical, state, file, leaf == "archived_sessions")) result.ImportedThreads++;
                    result.VerifiedThreads++;
                }
                if (finishIndex != null) finishIndex();
                if (!allClosed()) throw new IOException("Client started during organization.");
                settings.ShareLocalState = true; settings.ShareProjects = true; settings.SharedStateDirectory = state; settings.LocalHistoryLayout = 1; store.Save(settings);
                result.MovedFiles = journal.Moves.Count; journal.Stage = "complete"; Save(store, journal);
                try { PruneBackups(store, journal); } catch { /* Cleanup failure never invalidates a completed migration. */ }
                return result;
            } catch {
                if (finishIndex != null) finishIndex();
                try { Rollback(store, journal, allClosed); RestoreSettings(store, settings, journal); journal.Stage = "complete"; Save(store, journal); } catch { journal.Stage = "recovery-required"; Save(store, journal); }
                throw;
            }
        }
        static void Rollback(PrivateStore store, HistoryJournal journal, Func<bool> allClosed) {
            if (!allClosed()) throw new IOException("Close clients before recovery.");
            if (journal.Mode == "private") {
                foreach (var link in journal.Links.AsEnumerable().Reverse()) {
                    if (!allClosed()) throw new IOException("Client started during recovery.");
                    if (HistoryJunction.Matches(link.Path, link.Target)) continue;
                    if (Directory.Exists(link.Path)) { PlainTree(link.Path); Directory.Delete(link.Path, false); }
                    HistoryJunction.Create(link.Path, link.Target);
                }
                return;
            }
            foreach (var link in journal.Links.AsEnumerable().Reverse()) {
                if (!allClosed()) throw new IOException("Client started during recovery.");
                if (HistoryJunction.Target(link.Path) != null) HistoryJunction.Remove(link.Path, link.Target);
            }
            foreach (var move in journal.Moves.AsEnumerable().Reverse()) {
                if (!allClosed()) throw new IOException("Client started during recovery.");
                if (Directory.Exists(move.To)) { if (!Directory.Exists(move.From)) { Directory.CreateDirectory(Path.GetDirectoryName(move.From)); Directory.Move(move.To, move.From); } }
                else if (File.Exists(move.To) && !File.Exists(move.From)) { Directory.CreateDirectory(Path.GetDirectoryName(move.From)); File.Move(move.To, move.From); }
            }
            if (journal.IndexStarted) {
                foreach (string file in Directory.EnumerateFiles(journal.StateHome).Where(StateFile)) { if (!allClosed()) throw new IOException("Client started during recovery."); Safe.PlainPath(file); File.Delete(file); }
                foreach (string name in journal.Databases) { if (!allClosed()) throw new IOException("Client started during recovery."); File.Copy(Path.Combine(journal.BackupDirectory, "database", name), Path.Combine(journal.StateHome, name), true); }
            }
        }
        public static void Detach(PrivateStore store, Settings settings, string canonical, Func<bool> allClosed) {
            if (Pending(store) || !allClosed()) throw new HistorySetupRequiredException();
            canonical = Path.GetFullPath(canonical); Safe.PlainPath(canonical);
            var journal = new HistoryJournal { Id = Guid.NewGuid().ToString("N"), Mode = "private", Stage = "detaching", CanonicalHome = canonical, OldShared = settings.ShareLocalState, OldProjects = settings.ShareProjects, OldLayout = settings.LocalHistoryLayout, OldStateHome = settings.SharedStateDirectory, OldVersion = settings.Version };
            journal.BackupDirectory = Path.Combine(store.Root, "HistoryBackups", journal.Id); Directory.CreateDirectory(journal.BackupDirectory);
            MarkBackup(journal);
            try {
                foreach (var profile in settings.Accounts.Where(p => p.Id != "Default")) foreach (string leaf in Leaves) {
                    string path = Path.Combine(store.HomeFor(profile), leaf), target = Path.Combine(canonical, leaf);
                    if (HistoryJunction.Target(path) == null) continue;
                    if (!allClosed() || !HistoryJunction.Matches(path, target)) throw new IOException("Unexpected client or history link.");
                    journal.Links.Add(new HistoryLink { Path = path, Target = target }); Save(store, journal);
                    HistoryJunction.Remove(path, target); Directory.CreateDirectory(path);
                }
                settings.ShareLocalState = false; settings.ShareProjects = false; settings.LocalHistoryLayout = 1; store.Save(settings);
                journal.Stage = "complete"; Save(store, journal);
            } catch { try { Rollback(store, journal, allClosed); RestoreSettings(store, settings, journal); journal.Stage = "complete"; Save(store, journal); } catch { journal.Stage = "recovery-required"; Save(store, journal); } throw; }
        }
        static void RestoreSettings(PrivateStore store, Settings settings, HistoryJournal journal) {
            settings.ShareLocalState = journal.OldShared; settings.ShareProjects = journal.OldProjects; settings.SharedStateDirectory = journal.OldStateHome; settings.LocalHistoryLayout = journal.OldLayout; settings.Version = journal.OldVersion; store.Save(settings);
        }
        public static void Recover(PrivateStore store, Settings settings, Func<bool> allClosed) {
            if (!Pending(store)) return;
            var journal = ReadJournal(store);
            if (journal == null || !Regex.IsMatch(journal.Id ?? "", @"\A[0-9a-f]{32}\z") || journal.BackupDirectory != Path.Combine(store.Root, "HistoryBackups", journal.Id)) throw new IOException("Invalid recovery journal.");
            Safe.PlainPath(journal.BackupDirectory);
            var allowed = settings.Accounts.Where(p => p.Id != "Default").SelectMany(p => Leaves.Select(leaf => Path.Combine(store.HomeFor(p), leaf))).Concat(Leaves.Select(leaf => Path.Combine(journal.CanonicalHome, leaf))).ToArray();
            Func<string, bool> known = path => allowed.Any(parent => String.Equals(path, parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase)) || path.StartsWith(journal.BackupDirectory + "\\", StringComparison.OrdinalIgnoreCase);
            if (journal.Moves.Any(move => move == null || !Path.IsPathRooted(move.From ?? "") || !Path.IsPathRooted(move.To ?? "") || !known(Path.GetFullPath(move.From)) || !known(Path.GetFullPath(move.To))) || journal.Links.Any(link => link == null || !allowed.Contains(link.Path, StringComparer.OrdinalIgnoreCase) || !Leaves.Any(leaf => String.Equals(link.Target, Path.Combine(journal.CanonicalHome, leaf), StringComparison.OrdinalIgnoreCase)))) throw new IOException("Recovery paths are not recognized history locations.");
            try { Rollback(store, journal, allClosed); RestoreSettings(store, settings, journal); journal.Stage = "complete"; Save(store, journal); }
            catch { journal.Stage = "recovery-required"; Save(store, journal); throw; }
        }
    }
}
