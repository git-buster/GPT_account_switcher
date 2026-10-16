using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAccounts {
    public static class QuotaFreshness {
        public static DateTimeOffset? Latest(IEnumerable<Snapshot> snapshots, DateTimeOffset now) {
            DateTimeOffset? latest = null;
            foreach (var snapshot in snapshots) {
                DateTimeOffset at;
                if (snapshot == null || !DateTimeOffset.TryParseExact(snapshot.UpdatedAtUtc,
                    new[] { "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz" },
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at)
                    || at > now) continue;
                if (!latest.HasValue || at > latest.Value) latest = at;
            }
            return latest;
        }
    }
    public static class QuotaRefreshPolicy {
        public static readonly int[] Minutes = { 0, 5, 10, 30, 60, 120 };
        public static int Normalize(int value) { return Minutes.Contains(value) ? value : 0; }
        public static bool MayStart(bool busy, bool refreshing, bool pageOpen) { return !busy && !refreshing && !pageOpen; }
    }
    public sealed class QuotaRefreshRunner {
        readonly object gate = new object(); readonly Func<CancellationToken, Task> work;
        CancellationTokenSource active; Task current = Task.FromResult(0);
        public QuotaRefreshRunner(Func<CancellationToken, Task> action) { if (action == null) throw new ArgumentNullException("action"); work = action; }
        public bool Running { get { lock (gate) return active != null; } }
        public Task Start() {
            lock (gate) {
                if (active != null) return current;
                var source = new CancellationTokenSource(); var completion = new TaskCompletionSource<bool>(); active = source; current = completion.Task;
                Execute(source, completion); return completion.Task;
            }
        }
        // All outcomes are observed through the completion task returned by Start.
        async void Execute(CancellationTokenSource source, TaskCompletionSource<bool> completion) {
            Exception error = null; bool cancelled = false;
            try { await work(source.Token); } catch (OperationCanceledException) { cancelled = true; } catch (Exception failure) { error = failure; }
            finally { lock (gate) { if (active == source) active = null; source.Dispose(); } }
            if (cancelled) completion.TrySetCanceled(); else if (error != null) completion.TrySetException(error); else completion.TrySetResult(true);
        }
        public void Cancel() { lock (gate) if (active != null) active.Cancel(); }
        public async Task Stop() { Task running; lock (gate) { running = current; if (active != null) active.Cancel(); } try { await running; } catch (OperationCanceledException) { } }
    }
    public enum LaunchMode { Cancel, Open, CloseExisting, Parallel }
    public static class LaunchPolicy {
        public static bool NeedsChoice(int processCount) { if (processCount < 0) throw new ArgumentOutOfRangeException("processCount"); return processCount > 0; }
        public static bool MustClose(LaunchMode mode) { return mode == LaunchMode.CloseExisting; }
        public static bool MayOpen(LaunchMode mode, int processCount) {
            if (processCount < 0) throw new ArgumentOutOfRangeException("processCount");
            return mode == LaunchMode.Parallel || (mode == LaunchMode.Open || mode == LaunchMode.CloseExisting) && processCount == 0;
        }
    }
    public sealed class ClientProcessSummary {
        public int DesktopInstances, BackgroundWorkers, AuxiliaryProcesses;
        public bool Uncertain;
        public string Describe(UiText text) {
            if (Uncertain) return text["detected"];
            var parts = new List<string>();
            if (DesktopInstances > 0) parts.Add(text.F("instances", DesktopInstances));
            if (BackgroundWorkers > 0) parts.Add(text.F("workers", BackgroundWorkers));
            if (AuxiliaryProcesses > 0) parts.Add(text.F("helpers", AuxiliaryProcesses));
            return parts.Count == 0 ? text["noClients"] : String.Join(" · ", parts);
        }
        public string Description {
            get {
                if (Uncertain) return "检测到正在运行的 ChatGPT / Codex";
                var parts = new List<string>();
                if (DesktopInstances > 0) parts.Add(DesktopInstances + " 个桌面实例");
                if (BackgroundWorkers > 0) parts.Add(BackgroundWorkers + " 组后台任务");
                if (AuxiliaryProcesses > 0) parts.Add(AuxiliaryProcesses + " 个辅助进程");
                return parts.Count == 0 ? "没有正在运行的客户端" : String.Join(" · ", parts);
            }
        }
    }
    public static class ClientLifetime {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessEntry {
            public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public static Dictionary<int, int> ParentMap() {
            var parents = new Dictionary<int, int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0); if (snapshot == new IntPtr(-1)) return null;
            try {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                if (!Process32First(snapshot, ref entry)) return null;
                do { parents[(int)entry.Id] = (int)entry.Parent; } while (Process32Next(snapshot, ref entry));
            } finally { CloseHandle(snapshot); }
            return parents;
        }
        public static ClientProcessSummary Summarize(OfficialInstall official, IEnumerable<AppProcess> processes, IDictionary<int, int> parents) {
            var summary = new ClientProcessSummary();
            var running = processes.GroupBy(p => p.Id).Select(g => g.First()).ToArray();
            if (running.Length == 0) return summary;
            if (parents == null || running.Any(p => !parents.ContainsKey(p.Id))) { summary.Uncertain = true; return summary; }
            var ids = new HashSet<int>(running.Select(p => p.Id));
            foreach (var process in running) {
                int parent; parents.TryGetValue(process.Id, out parent); var seen = new HashSet<int> { process.Id }; bool child = false;
                while (parent > 0) {
                    if (!seen.Add(parent) || seen.Count > 200) { summary.Uncertain = true; return summary; }
                    if (ids.Contains(parent)) { child = true; break; }
                    if (!parents.TryGetValue(parent, out parent)) break;
                }
                if (child) summary.AuxiliaryProcesses++;
                else if (String.Equals(process.Image, official.Exe, StringComparison.OrdinalIgnoreCase)) summary.DesktopInstances++;
                else summary.BackgroundWorkers++;
            }
            if (summary.DesktopInstances + summary.BackgroundWorkers == 0) summary.Uncertain = true;
            return summary;
        }
        public static bool AccountedFor(IEnumerable<AppProcess> running, IEnumerable<AppProcess> known, string targetId, IDictionary<int, int> parents) {
            var roots = known.ToArray(); if (roots.Any(p => p.ProfileId == targetId)) return false;
            foreach (var process in running) {
                int id = process.Id; var seen = new HashSet<int>(); bool found = false;
                while (id > 0 && seen.Add(id) && seen.Count <= 200) {
                    if (roots.Any(p => p.Id == id && p.ProfileId != targetId)) { found = true; break; }
                    int parent; if (!parents.TryGetValue(id, out parent)) break; id = parent;
                }
                if (!found) return false;
            }
            return true;
        }
        public static bool CanEditSidebar(OfficialInstall official, PrivateStore store, Profile target) {
            var running = Inventory(official); if (running.Count == 0) return true;
            var known = official.KnownWindows(store); var parents = ParentMap(); if (parents == null) return false;
            return AccountedFor(running, known, target.Id, parents);
        }
        public static List<AppProcess> Inventory(OfficialInstall official) {
            var result = new List<AppProcess>(); int session, currentId; string ownImage;
            using (var self = Process.GetCurrentProcess()) { session = self.SessionId; currentId = self.Id; ownImage = self.MainModule.FileName; }
            foreach (string image in new[] { official.Exe, official.Cli })
                foreach (var live in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(image))) using (live) {
                    try {
                        if (live.HasExited || live.SessionId != session) continue;
                        if (!String.Equals(live.MainModule.FileName, image, StringComparison.OrdinalIgnoreCase)) continue;
                        var candidate = new AppProcess { Id = live.Id, Image = image, Started = live.StartTime.ToUniversalTime().ToFileTimeUtc(), SessionId = live.SessionId, HasWindow = live.MainWindowHandle != IntPtr.Zero };
                        if (MatchesTarget(candidate, candidate, new[] { official.Exe, official.Cli }, currentId, session) && !String.Equals(candidate.Image, ownImage, StringComparison.OrdinalIgnoreCase)) result.Add(candidate);
                    } catch (System.ComponentModel.Win32Exception) { if (!live.HasExited) throw; }
                    catch (InvalidOperationException) { if (!live.HasExited) throw; }
                }
            return result;
        }
        public static bool MatchesTarget(AppProcess expected, AppProcess actual, string[] allowedImages, int currentId, int currentSession) {
            if (expected == null || actual == null || allowedImages == null || expected.Id <= 0 || expected.Id == currentId || expected.Started <= 0) return false;
            return expected.Id == actual.Id && expected.Started == actual.Started && expected.SessionId == currentSession && actual.SessionId == currentSession && String.Equals(expected.Image, actual.Image, StringComparison.OrdinalIgnoreCase) && allowedImages.Any(image => !String.IsNullOrEmpty(image) && String.Equals(image, expected.Image, StringComparison.OrdinalIgnoreCase));
        }
        static bool EndVerified(AppProcess expected, OfficialInstall official) {
            var allowedImages = new[] { official.Exe, official.Cli };
            int currentId, currentSession; string ownImage;
            using (var self = Process.GetCurrentProcess()) { currentId = self.Id; currentSession = self.SessionId; ownImage = self.MainModule.FileName; }
            if (expected != null && String.Equals(expected.Image, ownImage, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            if (!MatchesTarget(expected, expected, allowedImages, currentId, currentSession)) throw new InvalidOperationException();
            Process live; try { live = Process.GetProcessById(expected.Id); } catch (ArgumentException) { return true; }
            using (live) {
                try {
                    if (live.HasExited) return true;
                    var actual = new AppProcess { Id = live.Id, SessionId = live.SessionId, Started = live.StartTime.ToUniversalTime().ToFileTimeUtc(), Image = live.MainModule.FileName };
                    if (!MatchesTarget(expected, actual, allowedImages, currentId, currentSession)) throw new IOException("Client identity changed; no process terminated.");
                    // Terminate this verified process only. Never terminate a process tree or Windows job.
                    live.Kill(); return true;
                } catch (InvalidOperationException) { if (live.HasExited) return true; throw; }
                catch (System.ComponentModel.Win32Exception) { if (live.HasExited) return true; throw; }
            }
        }
        public static bool StopAll(OfficialInstall official, bool endBackground, Action<string> progress) {
            var before = Inventory(official); if (before.Count == 0) return true;
            progress("正在关闭 ChatGPT / Codex…");
            // Send WM_CLOSE only to windows owned by verified official processes. No Restart Manager,
            // session-ending broadcast, process-tree termination, or job-wide termination is used.
            foreach (var p in before.Where(p => String.Equals(p.Image, official.Exe, StringComparison.OrdinalIgnoreCase))) ClientWindows.RequestClose(p);
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 1500) { if (Inventory(official).Count == 0) return true; Thread.Sleep(100); }
            if (!endBackground) return false;
            progress("正在结束残留的官方客户端后台进程…");
            // Bounded retries cover helper processes spawned while the app is shutting down.
            while (timer.ElapsedMilliseconds < 10000) {
                var remaining = Inventory(official); if (remaining.Count == 0) return true;
                foreach (var p in remaining.OrderBy(x => String.Equals(x.Image, official.Exe, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                    try { EndVerified(p, official); } catch (ArgumentException) { } catch (InvalidOperationException) { }
                Thread.Sleep(250);
            }
            return Inventory(official).Count == 0;
        }
    }
    public static class SwitchSequence {
        // Keep cleanup and the subsequent launch on the same worker, without a UI continuation,
        // status query or server synchronization between them. A prepared local sidebar patch
        // may be applied after shutdown, immediately before launch, while its profile is closed.
        public static bool CloseThenOpen(Func<bool> close, Action open) {
            if (!close()) return false;
            open(); return true;
        }
    }
    public enum ClientWindowKind { OfficialBrowser, OwnController }
    public static class ClientWindows {
        delegate bool EnumWindow(IntPtr window, IntPtr state);
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct Flash { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr state);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder value, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int show);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr window);
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref Flash info);
        [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(uint change, uint flags, string item, IntPtr unused);
        public static bool SameOwner(AppProcess expected, AppProcess actual, int currentSession) {
            return expected != null && actual != null && expected.Id > 0 && expected.Started > 0 && expected.Id == actual.Id && expected.Started == actual.Started && expected.SessionId == currentSession && actual.SessionId == currentSession && !String.IsNullOrEmpty(expected.Image) && String.Equals(expected.Image, actual.Image, StringComparison.OrdinalIgnoreCase);
        }
        static bool Live(AppProcess expected) {
            if (expected == null) return false;
            try { using (var p = Process.GetProcessById(expected.Id)) using (var self = Process.GetCurrentProcess()) {
                return !p.HasExited && SameOwner(expected, new AppProcess { Id = p.Id, SessionId = p.SessionId, Started = p.StartTime.ToUniversalTime().ToFileTimeUtc(), Image = p.MainModule.FileName }, self.SessionId);
            } } catch { return false; }
        }
        static bool Owns(IntPtr window, AppProcess process) {
            uint owner; return window != IntPtr.Zero && window != new IntPtr(0xffff) && GetWindowThreadProcessId(window, out owner) != 0 && owner == process.Id;
        }
        public static bool IsApplicationWindow(ClientWindowKind kind, string className, int titleLength, int style, int extendedStyle, int width, int height, bool minimized) {
            if (titleLength <= 0 || (style & 0x40000000) != 0 || (extendedStyle & 0x08000080) != 0) return false;
            bool matchingClass = kind == ClientWindowKind.OfficialBrowser ? className == "Chrome_WidgetWin_1" : kind == ClientWindowKind.OwnController && className != null && className.StartsWith("HwndWrapper[", StringComparison.Ordinal) && (style & 0x00c40000) != 0;
            return matchingClass && (minimized || width >= 200 && height >= 100);
        }
        static bool Candidate(IntPtr window, ClientWindowKind kind) {
            if (kind == ClientWindowKind.OwnController) {
                var source = System.Windows.Interop.HwndSource.FromHwnd(window);
                if (source == null || !(source.RootVisual is System.Windows.Window)) return false;
            }
            Rect rect; if (!GetWindowRect(window, out rect)) return false;
            var className = new StringBuilder(128); GetClassName(window, className, className.Capacity);
            // Inspect title length only. Do not fetch, log, or persist window-title contents.
            return IsApplicationWindow(kind, className.ToString(), GetWindowTextLength(window), GetWindowLong(window, -16), GetWindowLong(window, -20), rect.Right - rect.Left, rect.Bottom - rect.Top, IsIconic(window));
        }
        static List<IntPtr> Windows(AppProcess process, ClientWindowKind kind) {
            var windows = new List<IntPtr>(); if (!Live(process)) return windows;
            // Native WPF windows are supported only for this process's own controller windows.
            using (var self = Process.GetCurrentProcess()) if (kind == ClientWindowKind.OwnController && process.Id != self.Id) return windows;
            EnumWindows(delegate(IntPtr window, IntPtr state) {
                if (Owns(window, process) && Candidate(window, kind)) windows.Add(window);
                return true;
            }, IntPtr.Zero);
            return windows.OrderByDescending(IsWindowVisible).ToList();
        }
        public static bool Wake(AppProcess process) { return Wake(process, ClientWindowKind.OfficialBrowser); }
        public static bool CanRestoreDirect(ClientWindowKind kind, bool visible, bool minimized, bool enabled) {
            return enabled && (kind == ClientWindowKind.OwnController || kind == ClientWindowKind.OfficialBrowser && (visible || minimized));
        }
        static bool Ready(IntPtr window, ClientWindowKind kind) {
            if (!CanRestoreDirect(kind, IsWindowVisible(window), IsIconic(window), IsWindowEnabled(window))) return false;
            UIntPtr ignored;
            return SendMessageTimeout(window, 0, UIntPtr.Zero, IntPtr.Zero, 2, 100, out ignored) != IntPtr.Zero;
        }
        public static bool Wake(AppProcess process, ClientWindowKind kind) {
            // A closed Electron window can leave a native shell behind. Showing that shell
            // bypasses the application's reopen lifecycle and can produce a dead window.
            var window = Windows(process, kind).FirstOrDefault(w => Ready(w, kind)); if (window == IntPtr.Zero || !Live(process) || !Owns(window, process) || !Candidate(window, kind)) return false;
            ShowWindowAsync(window, IsIconic(window) ? 9 : 5);
            if (kind == ClientWindowKind.OfficialBrowser && Live(process) && Owns(window, process)) TaskbarPresentation.Ensure(window, process);
            RefreshIcons(process, kind);
            var popup = GetLastActivePopup(window); if (popup != window && Owns(popup, process) && IsWindowVisible(popup) && Candidate(popup, kind) && Ready(popup, kind)) window = popup;
            if (!SetForegroundWindow(window)) {
                var flash = new Flash { Size = (uint)Marshal.SizeOf(typeof(Flash)), Window = window, Flags = 3, Count = 3 }; FlashWindowEx(ref flash);
            }
            return kind == ClientWindowKind.OwnController || Live(process) && Owns(window, process) && IsWindowVisible(window) && Ready(window, kind);
        }
        public static bool RefreshIcons(AppProcess process, ClientWindowKind kind) {
            bool refreshed = false;
            foreach (var window in Windows(process, kind)) {
                foreach (uint size in new uint[] { 0, 1 }) {
                    if (!Live(process) || !Owns(window, process) || !Candidate(window, kind)) return false;
                    UIntPtr icon;
                    if (SendMessageTimeout(window, 0x007f, new UIntPtr(size), IntPtr.Zero, 2, 100, out icon) == IntPtr.Zero || icon == UIntPtr.Zero) continue;
                    // Read only: icon ownership and lifetime remain entirely with the client.
                    refreshed = true;
                }
            }
            if (refreshed && kind == ClientWindowKind.OfficialBrowser) SHChangeNotify(0x00002000, 0x0005, process.Image, IntPtr.Zero);
            return refreshed;
        }
        public static bool HasVisibleWindow(AppProcess process) { return HasVisibleWindow(process, ClientWindowKind.OfficialBrowser); }
        public static bool RepairTaskbarPresentation(AppProcess process) {
            bool applied = false;
            foreach (var window in Windows(process, ClientWindowKind.OfficialBrowser)) if (Ready(window, ClientWindowKind.OfficialBrowser) && Live(process) && Owns(window, process)) applied |= TaskbarPresentation.Ensure(window, process);
            return applied;
        }
        public static bool HasVisibleWindow(AppProcess process, ClientWindowKind kind) { return Windows(process, kind).Any(IsWindowVisible); }
        public static bool HasMinimizedWindow(AppProcess process) { return HasMinimizedWindow(process, ClientWindowKind.OfficialBrowser); }
        public static bool HasMinimizedWindow(AppProcess process, ClientWindowKind kind) { return Windows(process, kind).Any(IsIconic); }
        public static void RequestClose(AppProcess process) {
            if (process == null || process.Id == Process.GetCurrentProcess().Id) return;
            foreach (var window in Windows(process, ClientWindowKind.OfficialBrowser)) if (Live(process) && Owns(window, process) && Candidate(window, ClientWindowKind.OfficialBrowser)) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
    }
    public sealed class LocalSession : IDisposable {
        readonly Process process; int sequence;
        public int ProcessId { get { return process.Id; } }
        public LocalSession(OfficialInstall official, string home) : this(official, home, null) { }
        public LocalSession(OfficialInstall official, string home, string sqliteHome) {
            Safe.PlainPath(home); if (!Directory.Exists(home)) throw new IOException();
            var start = new ProcessStartInfo { FileName = official.Cli, Arguments = "app-server", WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            Safe.CleanEnvironment(start, home, sqliteHome); process = official.StartVerified(start); process.StandardError.ReadToEndAsync();
            try {
                Call("initialize", new { clientInfo = new { name = "local_project_switcher", version = "0.6.13" }, capabilities = new { experimentalApi = true } });
                process.StandardInput.WriteLine("{\"method\":\"initialized\",\"params\":{}}"); process.StandardInput.Flush();
            } catch { Dispose(); throw; }
        }
        public object Call(string method, object args) { return AccountQuery.Rpc(process, sequence++, method, args); }
        public object HistoryCall(string method, object args) { return AccountQuery.HistoryRpc(process, sequence++, method, args); }
        public void Dispose() { try { process.StandardInput.Close(); if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(1500); } } catch { } process.Dispose(); }
    }
    public sealed class AccountDataModeChangedException : IOException { }
    public sealed class StateDirectoryConflictException : IOException { }
    public static class LocalWorkState {
        static string LocalDirectory(string value) {
            string path = Path.GetFullPath(value); Safe.PlainPath(path);
            if (!Directory.Exists(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Network) throw new IOException("A local state directory is required.");
            return path;
        }
        public static string DirectoryFor(Settings settings) {
            if (!settings.ShareLocalState) return null;
            return LocalDirectory(settings.SharedStateDirectory);
        }
        public static string DirectoryForProfile(PrivateStore store, Settings settings, Profile profile) {
            if (settings.ShareLocalState) return DirectoryFor(settings);
            // The default keeps its original configuration. Other profiles explicitly select
            // their own state directory instead of depending on the parent's environment.
            if (profile.Id == "Default") return null;
            string home = store.HomeFor(profile);
            if (settings.LocalHistoryLayout == 1) { home = Path.Combine(home, "switcher-independent-state"); Safe.PlainPath(home); Directory.CreateDirectory(home); }
            return LocalDirectory(home);
        }
        public static bool CanReuse(string profileId, string recordedState, string requestedState) {
            // The shared source is the default profile's existing state, so its window needs no migration.
            return profileId == "Default" && (recordedState == null || requestedState == null) || String.Equals(recordedState, requestedState, StringComparison.OrdinalIgnoreCase);
        }
        public static string ResolveDefault(OfficialInstall official, PrivateStore store) {
            string home = store.HomeFor(new Profile { Id = "Default" });
            using (var session = new LocalSession(official, home)) {
                var config = Safe.Field(session.Call("config/read", new { includeLayers = false }), "config");
                string value = Safe.Text(Safe.Field(config, "sqlite_home"), 32760);
                if (!String.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value)) throw new IOException();
                string path = Path.GetFullPath(String.IsNullOrWhiteSpace(value) ? home : value);
                return LocalDirectory(path);
            }
        }
        public static void ValidateProfile(OfficialInstall official, PrivateStore store, Settings settings, Profile profile) {
            CompleteHistorySharing.Validate(store, settings, profile);
            string desired = DirectoryForProfile(store, settings, profile); if (desired == null) return;
            using (var session = new LocalSession(official, store.HomeFor(profile), desired)) {
                var config = Safe.Field(session.Call("config/read", new { includeLayers = false }), "config");
                string configured = Safe.Text(Safe.Field(config, "sqlite_home"), 32760);
                var requirements = Safe.Field(session.Call("configRequirements/read", new { }), "requirements");
                string required = Safe.Text(Safe.Field(requirements, "sqliteHome"), 32760);
                ValidateSelection(desired, configured, required);
            }
        }
        public static void ValidateSelection(string desired, params string[] configured) {
            foreach (string setting in configured)
                if (!String.IsNullOrWhiteSpace(setting) && (!Path.IsPathRooted(setting) || !String.Equals(Path.GetFullPath(setting).TrimEnd('\\'), desired.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    throw new StateDirectoryConflictException();
        }
    }
    public sealed class SharedProject {
        public string ServerId { get; set; }
        public string Name { get; set; }
        public string[] Roots { get; set; }
        public string Key { get; set; }
    }
    public sealed class ProjectSyncResult { public int CatalogCount, AddedCount, VerifiedCount; }
    public static class ProjectSync {
        public static string LastStage;
        public static string Key(IEnumerable<string> paths) {
            var normalized = paths.Select(p => Path.GetFullPath(p).TrimEnd('\\').ToUpperInvariant()).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n", normalized)))).Replace("-", "").ToLowerInvariant();
        }
        static SharedProject Project(object raw, bool legacy) {
            string name = Safe.Text(Safe.Field(raw, "name"), 200); if (String.IsNullOrWhiteSpace(name)) return null;
            var values = Safe.Field(raw, legacy ? "rootPaths" : "roots") as object[]; if (values == null || values.Length == 0 || values.Length > 100) return null;
            var roots = new List<string>();
            foreach (var value in values) {
                string path = Safe.Text(legacy ? value : Safe.Field(value, "path"), 32760);
                if (path == null || !Path.IsPathRooted(path)) return null;
                try { roots.Add(Path.GetFullPath(path)); } catch { return null; }
            }
            return new SharedProject { ServerId = legacy ? null : Safe.Text(Safe.Field(raw, "id"), 200), Name = name, Roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Key = Key(roots) };
        }
        public static List<SharedProject> Read(LocalSession session) {
            var result = new List<SharedProject>(); string cursor = null; var cursors = new HashSet<string>();
            for (int page = 0; page < 50; page++) {
                var response = session.Call("project/list", new { cursor = cursor, limit = 100 }); var data = Safe.Field(response, "data") as object[];
                if (data == null) throw new IOException();
                foreach (var item in data) { var p = Project(item, false); if (p != null) result.Add(p); }
                cursor = Safe.Text(Safe.Field(response, "nextCursor"), 4096); if (String.IsNullOrEmpty(cursor)) return result;
                if (!cursors.Add(cursor)) throw new IOException();
            }
            throw new IOException();
        }
        static IEnumerable<SharedProject> Legacy(string home) {
            string file = Path.Combine(home, ".codex-global-state.json"); Safe.PlainPath(file); if (!File.Exists(file)) return new SharedProject[0];
            if (new FileInfo(file).Length > 20971520) throw new IOException();
            var serializer = Safe.Json(); serializer.MaxJsonLength = 20971520;
            var records = Safe.Map(Safe.Field(serializer.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)), "local-projects")); var result = new List<SharedProject>();
            foreach (var record in records) { if (record.Key.StartsWith("g-p-", StringComparison.Ordinal)) continue; var p = Project(record.Value, true); if (p != null) result.Add(p); }
            return result;
        }
        public static ProjectSyncResult Run(OfficialInstall official, PrivateStore store, Settings settings, Profile target) {
            if (!settings.ShareProjects) return new ProjectSyncResult();
            LastStage = "ReadCatalog";
            string file = Path.Combine(store.Root, "projects.dat");
            var catalog = File.Exists(file) ? store.Decode<List<SharedProject>>(file) : new List<SharedProject>();
            if (catalog == null || catalog.Count > 5000 || catalog.Any(p => p == null || p.Roots == null || p.Roots.Length == 0 || p.Roots.Length > 100 || Safe.Text(p.Name, 200) == null || p.Roots.Any(r => r == null || !Path.IsPathRooted(r)) || p.Key != Key(p.Roots))) throw new IOException();
            var byKey = catalog.ToDictionary(p => p.Key, StringComparer.Ordinal); var ids = new[] { "Default", target.Id, settings.LastId }.Where(id => id != null).Distinct().ToArray();
            var sessions = new Dictionary<string, LocalSession>(StringComparer.OrdinalIgnoreCase);
            Func<string, string, LocalSession> sessionFor = (home, state) => {
                string key = home + "\0" + state;
                LocalSession session;
                if (!sessions.TryGetValue(key, out session)) { session = new LocalSession(official, home, state); sessions.Add(key, session); }
                return session;
            };
            try {
                foreach (string id in ids) {
                    var profile = settings.Accounts.FirstOrDefault(p => p.Id == id); if (profile == null) continue;
                    string home = store.HomeFor(profile); if (!Directory.Exists(home)) continue;
                    LastStage = "CaptureLocalProjects";
                    var projects = Read(sessionFor(home, null)); LastStage = "ReadLegacyFolderEntries";
                    foreach (var project in projects.Concat(Legacy(home)).GroupBy(p => p.Key).Select(g => g.First())) byKey[project.Key] = project;
                }
                LastStage = "WriteEncryptedCatalog"; if (byKey.Count > 5000) throw new IOException(); catalog = byKey.Values.ToList(); store.Encode(file, catalog); store.Prepare(target);
                var report = new ProjectSyncResult { CatalogCount = catalog.Count };
                var destination = sessionFor(store.HomeFor(target), LocalWorkState.DirectoryForProfile(store, settings, target));
                LastStage = "ReadTargetProjects";
                var existing = new HashSet<string>(Read(destination).Select(p => p.Key), StringComparer.Ordinal);
                foreach (var project in catalog) if (!existing.Contains(project.Key)) {
                    LastStage = "CreateTargetFolderEntry";
                    destination.Call("project/create", new { idempotencyKey = "local-switcher-" + project.Key, name = project.Name, roots = project.Roots.Select(r => new { path = r }).ToArray() });
                    existing.Add(project.Key); report.AddedCount++;
                }
                LastStage = "VerifyTargetProjects"; var verified = new HashSet<string>(Read(destination).Select(p => p.Key), StringComparer.Ordinal);
                report.VerifiedCount = catalog.Count(p => verified.Contains(p.Key)); if (report.VerifiedCount != catalog.Count) throw new IOException();
                LastStage = "Complete"; return report;
            } finally { foreach (var session in sessions.Values) session.Dispose(); }
        }
    }
    public sealed class SidebarPlan {
        public Dictionary<string, object> Projects = new Dictionary<string, object>();
        public Dictionary<string, string> ServerIds = new Dictionary<string, string>();
        public Dictionary<string, object> Assignments = new Dictionary<string, object>();
        public Dictionary<string, object> ThreadOrders = new Dictionary<string, object>();
        public Dictionary<string, string> Aliases = new Dictionary<string, string>();
        public List<string> Order = new List<string>();
    }
    public sealed class SidebarResult { public bool Changed, Deferred; public int ProjectCount; }
    public static class ProjectSidebarSync {
        public const string StateFile = ".codex-global-state.json";
        static System.Web.Script.Serialization.JavaScriptSerializer Json() {
            var serializer = Safe.Json(); serializer.MaxJsonLength = 20971520; serializer.RecursionLimit = 100; return serializer;
        }
        public static Dictionary<string, object> ReadState(string home) {
            string file = Path.Combine(home, StateFile); Safe.PlainPath(file);
            if (!File.Exists(file)) return new Dictionary<string, object>();
            if (new FileInfo(file).Length > 20971520) throw new IOException("Local sidebar state is too large.");
            var parsed = Json().DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
            if (parsed == null) throw new IOException("Local sidebar state is invalid."); return parsed;
        }
        static string[] Strings(object raw) { return (raw as object[] ?? new object[0]).Select(v => Safe.Text(v, 32760)).Where(v => v != null).ToArray(); }
        static Dictionary<string, object> LocalProject(string id, object raw) {
            var record = Safe.Map(raw); string[] roots = Strings(Safe.Field(raw, "rootPaths"));
            if (Safe.Text(id, 32760) == null || id.StartsWith("g-p-", StringComparison.Ordinal) || Safe.Field(raw, "id") as string != id || Safe.Text(Safe.Field(raw, "name"), 200) == null || roots.Length == 0 || roots.Length > 100) return null;
            if (record.ContainsKey("chatGptBacking") || record.ContainsKey("chatGptBackingCreation") || record.ContainsKey("retiredChatGptBacking")) return null;
            if (roots.Any(p => !Path.IsPathRooted(p) || p.StartsWith("\\\\", StringComparison.Ordinal))) return null;
            var result = new Dictionary<string, object> { { "id", id }, { "name", record["name"] }, { "rootPaths", roots } };
            foreach (string key in new[] { "createdAt", "updatedAt" }) { object value; if (record.TryGetValue(key, out value) && (value is int || value is long || value is decimal || value is double)) result[key] = value; }
            return result;
        }
        public static SidebarPlan Build(IEnumerable<Dictionary<string, object>> sources, IEnumerable<SharedProject> serverProjects) {
            var plan = new SidebarPlan(); var rootToId = new Dictionary<string, string>();
            var servers = serverProjects.Where(p => p.ServerId != null).ToList();
            var records = sources.ToList();
            foreach (var source in records) {
                var projects = Safe.Map(Safe.Field(source, "local-projects")); if (projects.Count > 5000) throw new IOException();
                // The default profile is first. Folder aliases from another profile use its canonical ID.
                foreach (var item in projects) {
                    var project = LocalProject(item.Key, item.Value); if (project == null) continue;
                    string key = ProjectSync.Key(Strings(project["rootPaths"])), canonical;
                    if (rootToId.TryGetValue(key, out canonical)) { if (canonical != item.Key) plan.Aliases[item.Key] = canonical; continue; }
                    string serverId = null;
                    foreach (var map in Safe.Map(Safe.Field(source, "app-server-project-id-by-legacy-project-id-by-host"))) {
                        if (!map.Key.StartsWith("local:", StringComparison.Ordinal)) continue;
                        string candidate = Safe.Field(map.Value, item.Key) as string;
                        if (servers.Any(p => p.ServerId == candidate && p.Key == key)) { serverId = candidate; break; }
                    }
                    if (serverId == null) serverId = servers.Where(p => p.Key == key).Select(p => p.ServerId).FirstOrDefault();
                    if (serverId == null) continue; // Never import a stale independent DB identity into common state.
                    rootToId.Add(key, item.Key); plan.Projects[item.Key] = project; plan.ServerIds[item.Key] = serverId;
                }
            }
            foreach (var source in records) {
                foreach (string raw in Strings(Safe.Field(source, "project-order"))) { string id = Alias(plan, raw); if (plan.Projects.ContainsKey(id) && !plan.Order.Contains(id)) plan.Order.Add(id); }
                foreach (var assignment in Safe.Map(Safe.Field(source, "thread-project-assignments"))) {
                    string id = Alias(plan, Safe.Field(assignment.Value, "projectId") as string);
                    string memberHost = Safe.Field(Safe.Field(source, "thread-project-membership-host-ids"), assignment.Key) as string;
                    if (memberHost != null && memberHost != "local") continue;
                    if (Safe.Field(assignment.Value, "projectKind") as string == "local" && id != null && plan.Projects.ContainsKey(id) && !plan.Assignments.ContainsKey(assignment.Key))
                        plan.Assignments[assignment.Key] = new Dictionary<string, object> { { "projectKind", "local" }, { "projectId", id } };
                }
                foreach (var order in Safe.Map(Safe.Field(source, "sidebar-project-thread-orders"))) {
                    string id = Alias(plan, order.Key); if (!plan.Projects.ContainsKey(id) || plan.ThreadOrders.ContainsKey(id)) continue;
                    plan.ThreadOrders[id] = new Dictionary<string, object> { { "threadIds", Strings(Safe.Field(order.Value, "threadIds")).Distinct().Take(5000).ToArray() } };
                }
            }
            foreach (string id in plan.Projects.Keys) if (!plan.Order.Contains(id)) plan.Order.Add(id);
            if (plan.Projects.Count > 5000 || plan.Assignments.Count > 50000) throw new IOException(); return plan;
        }
        static string Alias(SidebarPlan plan, string id) { string mapped; return id != null && plan.Aliases.TryGetValue(id, out mapped) ? mapped : id; }
        public static SidebarPlan Prepare(OfficialInstall official, PrivateStore store, Settings settings, Profile target) {
            if (!settings.ShareProjects || !settings.ShareLocalState) return null;
            var ordered = settings.Accounts.OrderBy(p => p.Id == "Default" ? 0 : p.Id == settings.LastId ? 1 : 2).ToArray();
            var sources = ordered.Where(p => Directory.Exists(store.HomeFor(p))).Select(p => ReadState(store.HomeFor(p))).ToArray();
            using (var session = new LocalSession(official, store.HomeFor(new Profile { Id = "Default" }), LocalWorkState.DirectoryFor(settings))) return Build(sources, ProjectSync.Read(session));
        }
        public static Dictionary<string, object> Merge(SidebarPlan plan, Dictionary<string, object> target, string targetHome) {
            // Copy only local display indexes. Keep every unrelated target field and its account state intact.
            var result = new Dictionary<string, object>(target);
            var projects = new Dictionary<string, object>(Safe.Map(Safe.Field(target, "local-projects")));
            foreach (string alias in plan.Aliases.Keys) if (LocalProject(alias, Safe.Field(Safe.Field(target, "local-projects"), alias)) != null) projects.Remove(alias);
            foreach (var project in plan.Projects) {
                object previous; if (projects.TryGetValue(project.Key, out previous) && LocalProject(project.Key, previous) == null) throw new IOException("A project identity conflicts with nonlocal state.");
                projects[project.Key] = project.Value;
            }
            result["local-projects"] = projects;
            result["project-order"] = plan.Order.Concat(Strings(Safe.Field(target, "project-order")).Select(id => Alias(plan, id))).Distinct().ToArray();
            var hosts = new Dictionary<string, object>(Safe.Map(Safe.Field(target, "app-server-project-id-by-legacy-project-id-by-host")));
            string host = "local:" + targetHome; var mapping = new Dictionary<string, object>(Safe.Map(Safe.Field(hosts, host)));
            foreach (string alias in plan.Aliases.Keys) mapping.Remove(alias);
            foreach (var pair in plan.ServerIds) mapping[pair.Key] = pair.Value;
            hosts[host] = mapping; result["app-server-project-id-by-legacy-project-id-by-host"] = hosts;
            var assignments = new Dictionary<string, object>(Safe.Map(Safe.Field(target, "thread-project-assignments")));
            foreach (var pair in assignments.ToArray()) if (Safe.Field(pair.Value, "projectKind") as string == "local") {
                string memberHost = Safe.Field(Safe.Field(target, "thread-project-membership-host-ids"), pair.Key) as string;
                if (memberHost != null && memberHost != "local") continue;
                string id = Alias(plan, Safe.Field(pair.Value, "projectId") as string);
                if (id != null && plan.Projects.ContainsKey(id)) assignments[pair.Key] = new Dictionary<string, object> { { "projectKind", "local" }, { "projectId", id } };
            }
            foreach (var pair in plan.Assignments) if (!assignments.ContainsKey(pair.Key)) assignments[pair.Key] = pair.Value;
            result["thread-project-assignments"] = assignments;
            var orders = new Dictionary<string, object>(Safe.Map(Safe.Field(target, "sidebar-project-thread-orders")));
            foreach (var pair in plan.ThreadOrders) if (!orders.ContainsKey(pair.Key)) orders[pair.Key] = pair.Value;
            result["sidebar-project-thread-orders"] = orders;
            var selected = Safe.Map(Safe.Field(target, "selected-project")); string selectedId = Safe.Field(selected, "projectId") as string;
            if (selectedId != null && selectedId != Alias(plan, selectedId) && Safe.Field(selected, "type") as string == "local") {
                var replacement = new Dictionary<string, object>(selected); replacement["projectId"] = Alias(plan, selectedId); result["selected-project"] = replacement;
            }
            return result;
        }
        public static SidebarResult Apply(PrivateStore store, Profile target, SidebarPlan plan, Func<bool> canWrite) {
            var report = new SidebarResult(); if (plan == null || plan.Projects.Count == 0) return report;
            report.ProjectCount = plan.Projects.Count;
            if (!canWrite()) { report.Deferred = true; return report; }
            string home = store.HomeFor(target), file = Path.Combine(home, StateFile); Safe.PlainPath(file);
            byte[] before = File.Exists(file) ? File.ReadAllBytes(file) : null;
            var state = ReadState(home); var after = Merge(plan, state, home);
            if (Json().Serialize(state) == Json().Serialize(after)) return report;
            string temp = file + ".switcher-" + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                if (!canWrite()) { report.Deferred = true; return report; }
                string backups = Path.Combine(store.Root, "Backups", "Sidebar"); Safe.PlainPath(backups); Directory.CreateDirectory(backups);
                string backup = Path.Combine(backups, Guid.NewGuid().ToString("N") + ".dat");
                // The complete previous JSON stays encrypted in the private backup directory.
                byte[] encrypted = System.Security.Cryptography.ProtectedData.Protect(before ?? new byte[0], null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                using (var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(encrypted, 0, encrypted.Length); output.Flush(true); }
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    byte[] data = Encoding.UTF8.GetBytes(Json().Serialize(after)); output.Write(data, 0, data.Length); output.Flush(true);
                }
                if (!canWrite() || (before == null ? File.Exists(file) : !File.Exists(file) || !before.SequenceEqual(File.ReadAllBytes(file)))) { report.Deferred = true; return report; }
                if (before == null) File.Move(temp, file); else File.Replace(temp, file, null);
                report.Changed = true; return report;
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
