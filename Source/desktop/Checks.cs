using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
namespace DesktopAccounts {
    static class NativeWindowCheck {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct Bounds { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern bool SetWindowText(IntPtr window, string value);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr window);
        public delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public struct WindowClass { public uint Style; public WindowProc Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string Menu, Name; }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern ushort RegisterClass(ref WindowClass value);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern bool UnregisterClass(string name, IntPtr instance);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern IntPtr CreateWindowEx(uint extra, string name, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnableWindow(IntPtr window, bool enabled);
    }
    public static class CheckProgram {
        [STAThread] public static void Main(string[] args) {
            if (args.Length == 1 && args[0] == "--console-window-probe") { Console.WriteLine(NativeWindowCheck.GetConsoleWindow() == IntPtr.Zero ? "no-console" : "unexpected-console"); return; }
            if (args.Length > 1 && args[0] == "--render-preview") { int count; Tests.Preview(args[1], args.Length > 2 && Int32.TryParse(args[2], out count) ? count : 2); return; }
            if (args.Length > 1 && args[0] == "--render-ui-previews") { Tests.UiPreviews(args[1]); return; }
            if (args.Length == 1 && args[0] == "--ui-preview") { Tests.UiPreviewWindow(); return; }
            if (args.Length == 1 && args[0] == "--verify-install") { Tests.VerifyInstall(); return; }
            if (args.Length == 2 && args[0] == "--verify-running-clients") { Tests.VerifyRunning(args[1]); return; }
            if (args.Length > 1 && args[0] == "--self-test") { Tests.Run(args[1]); return; }
            if (args.Length == 3 && args[0] == "--organize-fictional-history") { Tests.OrganizeFictionalHistory(args[1], args[2]); return; }
            Console.WriteLine("Use --self-test OUTPUT or --render-preview IMAGE [COUNT]. This development checker is separate from the desktop product.");
        }
    }
    public static class Tests {
        public static void OrganizeFictionalHistory(string folder, string clientExe) {
            try {
                folder = Path.GetFullPath(folder); Safe.PlainPath(folder);
                if (File.ReadAllText(Path.Combine(folder, ".synthetic-history-fixture")) != "Fictional history only") throw new IOException();
                var store = new PrivateStore(Path.Combine(folder, "private")); var state = new Settings();
                state.Accounts.Add(new Profile { Id = "Default", Name = "Fictional A" }); state.Accounts.Add(new Profile { Id = "A", Name = "Fictional B" }); state.Accounts.Add(new Profile { Id = "33333333333373338333333333333333", Name = "Fictional C" }); store.Save(state);
                if (clientExe == "prepare") { foreach (var profile in state.Accounts.Where(p => p.Id != "Default")) store.Prepare(profile); Console.WriteLine("Prepared synthetic private metadata only."); return; }
                var official = OfficialInstall.ResolveCandidate(clientExe);
                using (var held = official.HoldVerifiedFiles()) using (var indexer = new HistoryIndexSession(official)) {
                    var result = CompleteHistorySharing.Organize(store, state, Path.Combine(folder, "canonical"), Path.Combine(folder, "common-db"), () => true, key => { }, indexer.Check, indexer.Dispose);
                    Console.WriteLine("PASS production organizer and official indexer: accounts=" + result.Accounts + " histories=" + result.VerifiedThreads + " imports=" + result.ImportedThreads);
                }
            } catch (Exception error) { File.WriteAllText(Path.Combine(folder, "fixture-diagnostic.txt"), error.ToString()); Console.WriteLine("FAIL production history fixture: " + error.GetType().Name); Environment.ExitCode = 1; }
        }
        public static void VerifyRunning(string output) {
            try {
                var official = OfficialInstall.Find(); var fixture = new PrivateStore(Path.Combine(Path.GetFullPath(output), "discovery-" + Guid.NewGuid().ToString("N")));
                var found = official.InspectExisting(fixture, new Profile { Id = "Default", Name = "Synthetic default" });
                bool valid = found != null && found.ProfileId == "Default" && !found.StateUnverified && ClientLaunchIdentity.MatchesArguments(ClientLaunchIdentity.Arguments(found.Id), null) && ClientLaunchIdentity.HasPackageIdentity(found.Id, official.Family);
                Console.WriteLine(valid ? "PASS untracked official default window detected using an empty synthetic store; read-only, no wake/launch/sign-in/close" : "FAIL official external default window was not identified");
                if (!valid) Environment.ExitCode = 1;
            } catch { Environment.ExitCode = 1; Console.WriteLine("FAIL read-only external-window verification"); }
        }
        public static void VerifyInstall() {
            string link = Path.Combine(Path.GetTempPath(), "switcher-验证-é-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
            try {
                var detected = OfficialInstall.Find(); var manual = OfficialInstall.Find(new Settings { AppExePath = detected.Exe }); MakeShortcut(link, detected.Exe, "--ignored-test-option"); var fromLink = OfficialInstall.ResolveCandidate(link);
                bool valid = manual.Exe == detected.Exe && manual.Cli == detected.Cli && fromLink.Exe == detected.Exe && fromLink.Cli == detected.Cli && OfficialActivation.RegisteredFamily(detected.Exe) == detected.Family && OfficialActivation.ReadApplicationId(detected.Exe, detected.Family).StartsWith(detected.Family + "!", StringComparison.Ordinal);
                var versionStart = new ProcessStartInfo { FileName = fromLink.Cli, Arguments = "--version", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; Safe.CleanEnvironment(versionStart, null);
                using (var helper = fromLink.StartVerified(versionStart)) { var output = helper.StandardOutput.ReadToEndAsync(); var errors = helper.StandardError.ReadToEndAsync(); if (!helper.WaitForExit(10000)) { helper.Kill(); valid = false; } else valid &= helper.ExitCode == 0 && output.Wait(1000) && !String.IsNullOrWhiteSpace(output.Result); }
                Console.WriteLine(valid ? "PASS auto/manual/Unicode shortcut resolution and verified CLI process start (--version only; no GUI, sign-in or quota requests)" : "FAIL verification mismatch"); if (!valid) Environment.ExitCode = 1;
            } catch { Environment.ExitCode = 1; Console.WriteLine("FAIL official client verification"); }
            finally { if (File.Exists(link)) File.Delete(link); }
        }
        static Settings PreviewSettings() {
            var state = new Settings { ShareLocalState = true, ShareProjects = true };
            state.Accounts.Add(new Profile { Id = "Default", Name = "Account 1" }); state.Accounts.Add(new Profile { Id = Guid.NewGuid().ToString("N"), Name = "Account 2" }); state.LastId = state.Accounts[1].Id; return state;
        }
        static ClientProcessSummary PreviewProcesses() { return new ClientProcessSummary { DesktopInstances = 1, AuxiliaryProcesses = 12 }; }
        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) {
                var matching = child as T; if (matching != null) yield return matching;
                foreach (var descendant in Descendants<T>(child)) yield return descendant;
            }
        }
        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("00021401-0000-0000-C000-000000000046")] class ShortcutFixture { }
        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("000214F9-0000-0000-C000-000000000046"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        interface IShortcutFixture {
            void GetPath([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] StringBuilder value, int length, IntPtr data, uint flags);
            void GetIDList(out IntPtr value); void SetIDList(IntPtr value);
            void GetDescription([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] StringBuilder value, int length);
            void SetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string value);
            void GetWorkingDirectory([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] StringBuilder value, int length);
            void SetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string value);
            void GetArguments([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] StringBuilder value, int length);
            void SetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string value);
            void GetHotkey(out short value); void SetHotkey(short value); void GetShowCmd(out int value); void SetShowCmd(int value);
            void GetIconLocation([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] StringBuilder value, int length, out int index);
            void SetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string value, int index);
            void SetRelativePath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string value, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string value);
        }
        static void MakeShortcut(string filename, string target, string arguments) {
            object link = new ShortcutFixture();
            try { var shell = (IShortcutFixture)link; shell.SetPath(target); shell.SetArguments(arguments); ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(filename, true); }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
        }
        static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        static void PumpUntil(Func<bool> completed, int timeoutMilliseconds) {
            if (completed()) return;
            var watch = Stopwatch.StartNew(); var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += delegate { if (completed() || watch.ElapsedMilliseconds >= timeoutMilliseconds) frame.Continue = false; };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
        static RenderTargetBitmap Snapshot(FrameworkElement visual) {
            for (int pass = 0; pass < 3; pass++) { Application.Current.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ApplicationIdle); visual.UpdateLayout(); }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * 2), (int)Math.Ceiling(visual.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap;
        }
        static void Save(RenderTargetBitmap bitmap, string file) {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(file)) encoder.Save(stream);
        }
        public static void UiPreviews(string folder) {
            Directory.CreateDirectory(folder); var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var choice in UiText.Languages) {
                var state = PreviewSettings(); state.Language = choice.Id; var dashboard = new Dashboard(null, state, true); dashboard.Show(); Save(Snapshot(dashboard.Window), Path.Combine(folder, "panel-" + choice.Id + ".png"));
                var manage = dashboard.CreateManagementDialog(); var pending = dashboard.ShowPageAsync(manage); Save(Snapshot(dashboard.Window), Path.Combine(folder, "settings-" + choice.Id + ".png"));
                var bodyScroll = (ScrollViewer)manage.View.Children[1]; bodyScroll.ScrollToEnd(); Save(Snapshot(dashboard.Window), Path.Combine(folder, "about-" + choice.Id + ".png"));
                var sharingField = Descendants<CheckBox>(manage.View).First(); bodyScroll.ScrollToVerticalOffset(Math.Max(0, sharingField.TranslatePoint(new Point(), manage.Body).Y - 60)); Save(Snapshot(dashboard.Window), Path.Combine(folder, "sharing-" + choice.Id + ".png"));
                var pathField = Descendants<TextBox>(manage.View).Single(); bodyScroll.ScrollToVerticalOffset(Math.Max(0, pathField.TranslatePoint(new Point(), manage.Body).Y - 55)); Save(Snapshot(dashboard.Window), Path.Combine(folder, "app-" + choice.Id + ".png"));
                var quotaField = Descendants<ComboBox>(manage.View).Single(x => x.Name == "QuotaInterval"); bodyScroll.ScrollToVerticalOffset(Math.Max(0, quotaField.TranslatePoint(new Point(), manage.Body).Y - 60)); Save(Snapshot(dashboard.Window), Path.Combine(folder, "quota-" + choice.Id + ".png")); manage.Complete(false);
                CheckBox sharedChoice; var history = dashboard.CreateHistoryDialog(out sharedChoice); pending = dashboard.ShowPageAsync(history); Save(Snapshot(dashboard.Window), Path.Combine(folder, "history-" + choice.Id + ".png")); history.Complete(false);
                var launch = dashboard.CreateLaunchDialog(state.Accounts[1], PreviewProcesses(), mode => { }); pending = dashboard.ShowPageAsync(launch); Save(Snapshot(dashboard.Window), Path.Combine(folder, "launch-" + choice.Id + ".png")); launch.Complete(false);
                if (choice.Id == "en") { TextBox input; var name = dashboard.CreateNameDialog(new UiText("en")["add"], "Account 3", out input); pending = dashboard.ShowPageAsync(name); Save(Snapshot(dashboard.Window), Path.Combine(folder, "add-en.png")); name.Complete(false); }
                dashboard.Window.Close();
                var recoveryState = StartupStorage.Fresh(); recoveryState.Language = choice.Id; var recoveryPanel = new Dashboard(null, recoveryState, true, storageProblem: "storagePermission"); recoveryPanel.Show();
                Save(Snapshot(recoveryPanel.Window), Path.Combine(folder, "recovery-" + choice.Id + ".png")); var recoveryPage = recoveryPanel.CreateStorageDialog(); pending = recoveryPanel.ShowPageAsync(recoveryPage); Save(Snapshot(recoveryPanel.Window), Path.Combine(folder, "recovery-detail-" + choice.Id + ".png")); recoveryPage.Complete(false); recoveryPanel.Window.Close();
            }
            app.Shutdown();
        }
        public static void UiPreviewWindow() {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose }; var dashboard = new Dashboard(null, PreviewSettings(), true); app.MainWindow = dashboard.Window; dashboard.Show(); app.Run();
        }
        public static void Preview(string file, int count) {
            var state = new Settings();
            for (int i = 0; i < Math.Max(0, Math.Min(30, count)); i++) state.Accounts.Add(new Profile { Id = i == 0 ? "Default" : Guid.NewGuid().ToString("N"), Name = "Account " + (i + 1) });
            state.LastId = state.Accounts.Count > 1 ? state.Accounts[1].Id : null; var app = new Application(); var dashboard = new Dashboard(null, state, true); var w = dashboard.Window; w.Show(); w.UpdateLayout();
            app.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(w); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(file)) encoder.Save(stream); w.Close();
        }
        public static void Run(string output) {
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output); var lines = new List<string>();
            Action<string, bool> check = (name, ok) => { lines.Add((ok ? "PASS " : "FAIL ") + name); if (!ok) Environment.ExitCode = 1; };
            try {
                string root = Path.Combine(output, "private-" + Guid.NewGuid().ToString("N")); var store = new PrivateStore(root); var settings = store.Load();
                for (int i = 0; i < 4; i++) settings.Accounts.Add(new Profile { Id = Guid.NewGuid().ToString("N"), Name = "Synthetic account " + i }); store.Save(settings);
                check("more than two accounts survive encrypted roundtrip", store.Load().Accounts.Count == 5);
                check("new settings do not silently enable shared working state", !settings.ShareLocalState && settings.SharedStateDirectory == null && LocalWorkState.DirectoryFor(settings) == null);
                check("new installations keep project sharing opt-in", !settings.ShareProjects);
                bool incompleteHistoryBlocked = false; try { CompleteHistorySharing.Validate(null, new Settings { ShareLocalState = true }, new Profile { Id = "Default" }); } catch (HistorySetupRequiredException) { incompleteHistoryBlocked = true; }
                check("legacy index-only sharing requires full organization before launching shared clients", incompleteHistoryBlocked);
                string brokenStartupRoot = Path.Combine(root, "unreadable-startup-fixture"), recoveryStartupRoot = Path.Combine(root, "recovery-startup-fixture"); File.WriteAllText(brokenStartupRoot, "fictional blocked directory");
                var recoveredStartup = StartupStorage.Open(brokenStartupRoot, recoveryStartupRoot);
                check("unavailable original storage opens an independent writable recovery workspace", recoveredStartup.Store != null && recoveredStartup.Problem != null && recoveredStartup.Store.Root == recoveryStartupRoot && !recoveredStartup.Settings.ShareLocalState && File.ReadAllText(brokenStartupRoot) == "fictional blocked directory");
                var impossibleStartup = StartupStorage.Open(brokenStartupRoot, brokenStartupRoot);
                check("no writable workspace still returns usable panel settings", impossibleStartup.Store == null && impossibleStartup.Problem != null && impossibleStartup.Settings.Accounts.Count == 1);
                check("storage explanations distinguish permissions, encryption and format", StartupStorage.Describe(new UnauthorizedAccessException()) == "storagePermission" && StartupStorage.Describe(new CryptographicException()) == "storageEncryption" && StartupStorage.Describe(new InvalidDataException()) == "storageFormat");
                string futureStartupRoot = Path.Combine(root, "future-startup-fixture"); var futureStartupStore = new PrivateStore(futureStartupRoot); futureStartupStore.Encode(Path.Combine(futureStartupRoot, "accounts.dat"), new Settings { Version = 99 }); byte[] futureStartupBytes = File.ReadAllBytes(Path.Combine(futureStartupRoot, "accounts.dat"));
                var futureStartup = StartupStorage.Open(futureStartupRoot, Path.Combine(root, "future-recovery-fixture"));
                check("unknown metadata format opens recovery without resetting original metadata", futureStartup.Problem == "storageFormat" && futureStartup.Store != null && File.ReadAllBytes(Path.Combine(futureStartupRoot, "accounts.dat")).SequenceEqual(futureStartupBytes));
                check("new installations default to English without reading host locale", settings.Language == "en" && settings.AppExePath == null);
                check("new installations leave timed refresh off", settings.QuotaRefreshMinutes == 0);
                check("existing settings do not silently perform an offline history migration", settings.LocalHistoryLayout == 0);
                string historyRoot = Path.Combine(root, "history-tests"), canonicalHistory = Path.Combine(historyRoot, "canonical"), commonDb = Path.Combine(historyRoot, "common-db");
                Directory.CreateDirectory(canonicalHistory); Directory.CreateDirectory(commonDb);
                var historyStore = new PrivateStore(Path.Combine(historyRoot, "private-store"));
                var historyState = new Settings(); historyState.Accounts.Add(new Profile { Id = "Default", Name = "Fictional source" });
                var hb = new Profile { Id = Guid.NewGuid().ToString("N"), Name = "Fictional B" }; var hc = new Profile { Id = Guid.NewGuid().ToString("N"), Name = "Fictional C" }; historyState.Accounts.Add(hb); historyState.Accounts.Add(hc); historyStore.Prepare(hb); historyStore.Prepare(hc); historyStore.Save(historyState);
                string sessionRelative = Path.Combine("sessions", "2000", "01", "01");
                Directory.CreateDirectory(Path.Combine(canonicalHistory, sessionRelative)); Directory.CreateDirectory(Path.Combine(historyStore.HomeFor(hb), sessionRelative));
                string firstHistory = "rollout-2000-01-01T00-00-00-11111111-1111-7111-8111-111111111111.jsonl", secondHistory = "rollout-2000-01-01T00-00-01-22222222-2222-7222-8222-222222222222.jsonl";
                string originalHistory = Path.Combine(canonicalHistory, sessionRelative, firstHistory), independentHistory = Path.Combine(historyStore.HomeFor(hb), sessionRelative, secondHistory);
                File.WriteAllText(originalHistory, "fictional first record\n"); File.WriteAllText(independentHistory, "fictional second record\n");
                string authMarker = Path.Combine(historyStore.HomeFor(hb), "synthetic-sign-in-marker.txt"); File.WriteAllText(authMarker, "fictional auth boundary");
                File.WriteAllText(Path.Combine(commonDb, "state_5.sqlite"), "fictional database snapshot");
                var organizedHistory = CompleteHistorySharing.Organize(historyStore, historyState, canonicalHistory, commonDb, () => true, key => { }, (home, db, file, archived) => false);
                check("history organization handles more than two independently signed-in accounts", organizedHistory.Accounts == 3 && organizedHistory.VerifiedThreads == 2 && historyStore.Load().LocalHistoryLayout == 1);
                check("complete-history settings prevent older switchers from changing the data mode", historyStore.Load().Version == 3);
                check("history organization moves original records rather than copying each account", !File.Exists(independentHistory + ".copy") && File.Exists(Path.Combine(canonicalHistory, sessionRelative, secondHistory)) && HistoryJunction.Matches(Path.Combine(historyStore.HomeFor(hb), "sessions"), Path.Combine(canonicalHistory, "sessions")) && HistoryJunction.Matches(Path.Combine(historyStore.HomeFor(hc), "sessions"), Path.Combine(canonicalHistory, "sessions")));
                check("linked profiles immediately see the same newer history bytes", File.ReadAllText(independentHistory) == "fictional second record\n");
                File.AppendAllText(independentHistory, "fictional B append\n");
                check("writes through one account reach the single canonical file", File.ReadAllText(Path.Combine(canonicalHistory, sessionRelative, secondHistory)).Contains("fictional B append"));
                check("organization leaves separate sign-in markers untouched", File.ReadAllText(authMarker) == "fictional auth boundary" && !File.Exists(Path.Combine(canonicalHistory, "synthetic-sign-in-marker.txt")));
                check("writer coordination locks are shared across profiles", HistoryJunction.Matches(Path.Combine(historyStore.HomeFor(hb), "thread-writer-locks"), Path.Combine(canonicalHistory, "thread-writer-locks")));
                string emptyHistoryHome = Path.Combine(historyRoot, "new-account-after-quota-check"); Directory.CreateDirectory(Path.Combine(emptyHistoryHome, "sessions")); Directory.CreateDirectory(Path.Combine(emptyHistoryHome, "thread-writer-locks")); File.WriteAllText(Path.Combine(emptyHistoryHome, "thread-writer-locks", ".coordination.lock"), "");
                CompleteHistorySharing.AttachEmptyHome(emptyHistoryHome, canonicalHistory, () => true);
                check("new accounts auto-attach even after an empty quota-helper initialization", HistoryJunction.Matches(Path.Combine(emptyHistoryHome, "sessions"), Path.Combine(canonicalHistory, "sessions")) && HistoryJunction.Matches(Path.Combine(emptyHistoryHome, "thread-writer-locks"), Path.Combine(canonicalHistory, "thread-writer-locks")));
                string existingHistoryHome = Path.Combine(historyRoot, "existing-independent-history"); Directory.CreateDirectory(Path.Combine(existingHistoryHome, "sessions")); File.WriteAllText(Path.Combine(existingHistoryHome, "sessions", "existing.jsonl"), "fictional private history"); bool existingNeedsOffline = false;
                try { CompleteHistorySharing.AttachEmptyHome(existingHistoryHome, canonicalHistory, () => true); } catch (HistorySetupRequiredException) { existingNeedsOffline = true; }
                check("auto-attach never silently moves existing independent history while other accounts run", existingNeedsOffline && File.ReadAllText(Path.Combine(existingHistoryHome, "sessions", "existing.jsonl")) == "fictional private history");
                bool historyBusy = false; try { CompleteHistorySharing.Organize(historyStore, historyState, canonicalHistory, commonDb, () => false, key => { }, (home, db, file, archived) => false); } catch (IOException) { historyBusy = true; }
                check("offline history organization refuses to run while clients remain", historyBusy);
                CompleteHistorySharing.Detach(historyStore, historyState, canonicalHistory, () => true);
                check("disabling complete history removes only managed links and preserves common records", !historyState.ShareLocalState && !historyState.ShareProjects && HistoryJunction.Target(Path.Combine(historyStore.HomeFor(hb), "sessions")) == null && File.Exists(originalHistory) && !File.Exists(independentHistory));
                check("independent mode selects a separate state database after detaching", LocalWorkState.DirectoryForProfile(historyStore, historyState, hb) == Path.Combine(historyStore.HomeFor(hb), "switcher-independent-state"));
                Directory.CreateDirectory(Path.GetDirectoryName(independentHistory)); File.WriteAllText(independentHistory, "conflicting independent version\n");
                bool historyConflict = false; try { CompleteHistorySharing.Organize(historyStore, historyState, canonicalHistory, commonDb, () => true, key => { }, (home, db, file, archived) => false); } catch (HistoryConflictException) { historyConflict = true; }
                check("divergent history is rejected and both versions remain intact", historyConflict && File.ReadAllText(independentHistory) == "conflicting independent version\n" && File.ReadAllText(Path.Combine(canonicalHistory, sessionRelative, secondHistory)).Contains("fictional B append") && !CompleteHistorySharing.Pending(historyStore));
                File.WriteAllText(independentHistory, File.ReadAllText(Path.Combine(canonicalHistory, sessionRelative, secondHistory)) + "new offline record\n");
                bool historyIndexFailed = false; try { CompleteHistorySharing.Organize(historyStore, historyState, canonicalHistory, commonDb, () => true, key => { }, (home, db, file, archived) => { File.WriteAllText(Path.Combine(commonDb, "state_5.sqlite"), "changed during fictional import"); throw new IOException("fictional import failure"); }); } catch (IOException) { historyIndexFailed = true; }
                check("failed index import rolls back moved files, links, settings and database state", historyIndexFailed && File.ReadAllText(Path.Combine(commonDb, "state_5.sqlite")) == "fictional database snapshot" && File.ReadAllText(independentHistory).EndsWith("new offline record\n") && HistoryJunction.Target(Path.Combine(historyStore.HomeFor(hb), "sessions")) == null && !historyStore.Load().ShareLocalState && !CompleteHistorySharing.Pending(historyStore));
                bool recoveryClosed = true, deferredRecovery = false;
                try { CompleteHistorySharing.Organize(historyStore, historyState, canonicalHistory, commonDb, () => recoveryClosed, key => { }, (home, db, file, archived) => { recoveryClosed = false; File.WriteAllText(Path.Combine(commonDb, "state_5.sqlite"), "fictional pending recovery"); throw new IOException("fictional client reopened during import"); }); } catch (IOException) { deferredRecovery = true; }
                check("a reopened client defers rollback and leaves an encrypted recovery journal", deferredRecovery && CompleteHistorySharing.Pending(historyStore));
                recoveryClosed = true; CompleteHistorySharing.Recover(historyStore, historyState, () => recoveryClosed);
                check("a later offline run recovers from durable incremental actions", !CompleteHistorySharing.Pending(historyStore) && !historyStore.Load().ShareLocalState && File.ReadAllText(Path.Combine(commonDb, "state_5.sqlite")) == "fictional database snapshot" && File.ReadAllText(independentHistory).EndsWith("new offline record\n"));
                var cleanedHistory = CompleteHistorySharing.Organize(historyStore, historyState, canonicalHistory, commonDb, () => true, key => { }, (home, db, file, archived) => false);
                check("successful organization retains one marked recovery backup rather than accumulating copies", Directory.GetDirectories(Path.Combine(historyStore.Root, "HistoryBackups")).Length == 1 && Directory.Exists(cleanedHistory.BackupDirectory));
                string startupFixture = Path.Combine(root, "Fictional app", "GPTSwitcher.exe");
                check("Windows startup command quotes the EXE and requests tray-only startup", StartupRegistration.Command(startupFixture) == "\"" + Path.GetFullPath(startupFixture) + "\" --background");
                check("startup removal matches only this copy's exact command", StartupRegistration.Matches(StartupRegistration.Command(startupFixture), startupFixture) && !StartupRegistration.Matches(StartupRegistration.Command(startupFixture), Path.Combine(root, "Other app", "GPTSwitcher.exe")));
                check("startup ownership rejects extra arguments and unrelated entries", !StartupRegistration.Matches(StartupRegistration.Command(startupFixture) + " --extra", startupFixture) && !StartupRegistration.Matches(null, startupFixture));
                string fixtureDirectory = Path.Combine(root, "synthetic desktop");
                check("manual default launch is recognized without a switcher record", ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe" }, null));
                check("unknown command line never identifies an account", !ClientLaunchIdentity.MatchesArguments(null, null));
                check("render and GPU helpers never identify as default account windows", !ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe", "--type=renderer" }, null) && !ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe", "--type", "gpu-process" }, null));
                check("explicit private profile cannot be mistaken for default", !ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe", "--user-data-dir=" + fixtureDirectory }, null));
                check("manual extra-account selector must match the exact private directory", ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe", "--user-data-dir=" + fixtureDirectory }, fixtureDirectory) && !ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe", "--user-data-dir=" + fixtureDirectory + "-other" }, fixtureDirectory));
                check("space-separated Unicode profile selectors work", ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe", "--user-data-dir", Path.Combine(root, "虚构 应用-é") }, Path.Combine(root, "虚构 应用-é")));
                check("a default launch cannot be assigned to a private account", !ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe" }, fixtureDirectory));
                check("ambiguous and missing profile selector values are rejected", !ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe", "--user-data-dir" }, null) && !ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe", "--user-data-dir=" + fixtureDirectory, "--user-data-dir=" + fixtureDirectory }, fixtureDirectory));
                check("relative or malformed profile options are not adopted", !ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe", "--user-data-dir=relative" }, fixtureDirectory) && !ClientLaunchIdentity.MatchesArguments(new[] { "ChatGPT.exe", "--user-data-directory=unexpected" }, null));
                var entry = new AppProcess { Id = 101 }; var separate = new AppProcess { Id = 102 };
                check("normal Windows application entry wins over a separate default process", ReferenceEquals(ClientLaunchIdentity.Choose(new[] { entry, separate }, true, item => item.Id == 101), entry));
                check("multiple ambiguous default instances require resolution", ClientLaunchIdentity.Choose(new[] { entry, separate }, true, item => true) == null && ClientLaunchIdentity.Choose(new[] { entry, separate }, true, item => false) == null);
                check("extra account selection cannot depend on the taskbar icon or package identity", ClientLaunchIdentity.Choose(new[] { entry, separate }, false, item => true) == null && ReferenceEquals(ClientLaunchIdentity.Choose(new[] { separate }, false, item => false), separate));
                bool bad = false;
                string packageRoot = Path.Combine(root, "FictionalPackage"), packageExe = Path.Combine(packageRoot, "app", "ChatGPT.exe"), packageFamily = "OpenAI.Codex_abcdefghijklm";
                string manifest = "<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10'><Applications><Application Id='App' Executable='app\\ChatGPT.exe'/></Applications></Package>";
                check("registered activation matches only the selected official executable", OfficialActivation.ApplicationId(manifest, packageRoot, packageExe, packageFamily) == packageFamily + "!App");
                bad = false; try { OfficialActivation.ApplicationId(manifest, packageRoot, Path.Combine(root, "Other", "ChatGPT.exe"), packageFamily); } catch (IOException) { bad = true; } check("registered activation cannot override a different manually selected installation", bad);
                bad = false; try { OfficialActivation.ApplicationId(manifest.Replace("app\\ChatGPT.exe", "..\\ChatGPT.exe"), packageRoot, Path.Combine(root, "ChatGPT.exe"), packageFamily); } catch (IOException) { bad = true; } check("registered activation rejects package path escape", bad);
                bad = false; try { OfficialActivation.ApplicationId(manifest.Replace("Id='App'", "Id='App!other'"), packageRoot, packageExe, packageFamily); } catch (IOException) { bad = true; } check("registered activation rejects malformed application identifiers", bad);
                bad = false; try { OfficialActivation.ApplicationId(manifest, packageRoot, packageExe, "Other.Vendor_abcdefghijklm"); } catch (IOException) { bad = true; } check("registered activation rejects an unrelated package family", bad);
                bad = false; try { OfficialActivation.ApplicationId(manifest.Replace("</Applications>", "<Application Id='Duplicate' Executable='app\\ChatGPT.exe'/></Applications>"), packageRoot, packageExe, packageFamily); } catch (IOException) { bad = true; } check("registered activation fails closed for ambiguous application entries", bad);
                bad = false; try { OfficialActivation.ApplicationId("<!DOCTYPE Package [<!ENTITY data SYSTEM 'file:///fictional-private-file'>]>" + manifest, packageRoot, packageExe, packageFamily); } catch (System.Xml.XmlException) { bad = true; } check("registered manifest parsing rejects external entities without reading files", bad);
                bad = false; try { OfficialActivation.ApplicationId(new string('x', 1048577), packageRoot, packageExe, packageFamily); } catch (IOException) { bad = true; } check("registered manifest parsing is size bounded", bad);
                check("only repository and feedback have fixed public URLs; author has no destination", ProjectLinks.Author == "git-buster" && Enum.GetValues(typeof(ProjectLink)).Length == 2 && ProjectLinks.Address(ProjectLink.Repository) == "https://github.com/git-buster/GPT_account_switcher" && ProjectLinks.Address(ProjectLink.Feedback) == "https://github.com/git-buster/GPT_account_switcher/issues/new");
                bool unknownLinkRejected = false; try { ProjectLinks.Address((ProjectLink)99); } catch (ArgumentOutOfRangeException) { unknownLinkRejected = true; } check("unknown link identifiers cannot open arbitrary destinations", unknownLinkRejected);
                check("quota intervals have exactly the requested six values", QuotaRefreshPolicy.Minutes.SequenceEqual(new[] { 0, 5, 10, 30, 60, 120 }));
                foreach (int minutes in QuotaRefreshPolicy.Minutes) { settings.QuotaRefreshMinutes = minutes; store.Save(settings); check("quota interval survives encrypted roundtrip: " + minutes, store.Load().QuotaRefreshMinutes == minutes); }
                settings.QuotaRefreshMinutes = -1; Safe.Validate(settings); check("invalid refresh intervals safely disable the timer", settings.QuotaRefreshMinutes == 0);
                check("refresh never overlaps switching, another refresh or a settings page", QuotaRefreshPolicy.MayStart(false, false, false) && !QuotaRefreshPolicy.MayStart(true, false, false) && !QuotaRefreshPolicy.MayStart(false, true, false) && !QuotaRefreshPolicy.MayStart(false, false, true));
                var lowerEnvironment = new ProcessStartInfo(); lowerEnvironment.EnvironmentVariables["codex_sqlite_home"] = "fictional stale override"; lowerEnvironment.EnvironmentVariables["openai_api_key"] = "fictional-key"; lowerEnvironment.EnvironmentVariables["chatgpt_fixture"] = "fictional"; lowerEnvironment.EnvironmentVariables["rust_log"] = "fictional";
                Safe.CleanEnvironment(lowerEnvironment, null); check("remove account overrides regardless of environment key casing", new[] { "codex_sqlite_home", "openai_api_key", "chatgpt_fixture", "rust_log" }.All(key => lowerEnvironment.EnvironmentVariables[key] == null));
                lowerEnvironment.EnvironmentVariables["node_options"] = "--require=fictional-module"; lowerEnvironment.EnvironmentVariables["NODE_PATH"] = "fictional-module-folder"; lowerEnvironment.EnvironmentVariables["ELECTRON_RUN_AS_NODE"] = "1"; Safe.CleanEnvironment(lowerEnvironment, null);
                check("official client cannot inherit developer runtime injection or node-only mode", new[] { "NODE_OPTIONS", "NODE_PATH", "ELECTRON_RUN_AS_NODE" }.All(key => lowerEnvironment.EnvironmentVariables[key] == null));
                string exeFixture = Path.Combine(root, "fingerprint.exe"), helperFixture = Path.Combine(root, "fingerprint-helper.exe"); File.WriteAllText(exeFixture, "synthetic executable"); File.WriteAllText(helperFixture, "synthetic helper");
                string exeHash, helperHash; var originalTime = File.GetLastWriteTimeUtc(exeFixture);
                using (var locked = new ClientBundleFiles(exeFixture, helperFixture)) {
                    exeHash = locked.ExeHash; helperHash = locked.CliHash; bool writeDenied = false;
                    try { File.WriteAllText(exeFixture, "changed synthetic executable"); } catch (IOException) { writeDenied = true; }
                    check("verified executable remains read-locked through process creation", writeDenied);
                }
                var guardedBundle = new ClientBundleGuard(exeFixture, helperFixture, exeHash, helperHash);
                var firstLease = guardedBundle.Acquire(); var secondLease = guardedBundle.Acquire();
                check("nested operation leases hash unchanged files only once", guardedBundle.VerificationCount == 1);
                firstLease.Dispose(); firstLease.Dispose(); bool leaseWriteDenied = false;
                try { File.WriteAllText(exeFixture, "replacement while locked"); } catch (IOException) { leaseWriteDenied = true; }
                check("disposing one lease cannot release another action's read lock", leaseWriteDenied);
                secondLease.Dispose(); using (guardedBundle.Acquire()) check("a new operation revalidates file contents", guardedBundle.VerificationCount == 2);
                File.WriteAllText(exeFixture, "changed synthetic executable"); File.SetLastWriteTimeUtc(exeFixture, originalTime);
                using (var changed = new ClientBundleFiles(exeFixture, helperFixture)) check("content replacement is detected even when the old timestamp is restored", !changed.Matches(exeHash, helperHash));
                bad = false; try { guardedBundle.Acquire().Dispose(); } catch (IOException) { bad = true; } check("fast operation leases still reject changed content with restored timestamps", bad);
                bool unverified = false; try { new OfficialInstall { Exe = exeFixture, Cli = helperFixture }.StartVerified(new ProcessStartInfo(exeFixture)); } catch (IOException) { unverified = true; }
                check("unverified selections cannot reach process execution", unverified);
                string fakeApp = Path.Combine(root, "shortcut 应用-é", "ChatGPT.exe"); Directory.CreateDirectory(Path.GetDirectoryName(fakeApp)); File.WriteAllText(fakeApp, "synthetic, not executable");
                string linkPath = Path.Combine(root, "fictional-app.lnk"); MakeShortcut(linkPath, fakeApp, "ignored-fixture-arguments");
                string resolvedLink = ClientShortcut.Read(linkPath, target => null); check("ordinary desktop shortcuts are read without executing them", resolvedLink == fakeApp);
                bool invalidLink = false; try { OfficialInstall.ResolveCandidate(linkPath); } catch (IOException) { invalidLink = true; }
                check("a shortcut to an unsigned or incomplete bundle still fails verification", invalidLink);
                string family = "OpenAI.Codex_example";
                check("Store shortcut identity is restricted to the discovered official family", ClientShortcut.MatchesStoreApp("shell:AppsFolder\\" + family + "!App", family) && !ClientShortcut.MatchesStoreApp("shell:AppsFolder\\Other.App!App", family) && !ClientShortcut.MatchesStoreApp("shell:AppsFolder\\" + family + "!App/extra", family));
                using (var alreadyCancelled = new System.Threading.CancellationTokenSource()) { alreadyCancelled.Cancel(); bool stopped = false; try { AccountQuery.Read(null, null, null, alreadyCancelled.Token); } catch (OperationCanceledException) { stopped = true; } check("cancelled quota work stops before accessing any profile or starting any helper", stopped); }
                check("all five language catalogs have every entry", UiText.Languages.Length == 5 && UiText.Complete);
                foreach (var choice in UiText.Languages) foreach (var key in UiText.Keys) {
                    var formatted = new UiText(choice.Id).F(key, "1", "2", "3", "4"); if (String.IsNullOrWhiteSpace(formatted)) throw new InvalidDataException("Empty translation.");
                }
                check("all translated message formats are valid", true);
                settings.Language = "ja"; settings.AppExePath = Path.Combine(root, "bundle", "ChatGPT.exe"); store.Save(settings);
                check("language and optional app location survive encrypted roundtrip", store.Load().Language == "ja" && store.Load().AppExePath == settings.AppExePath);
                settings.Language = "unsupported-language"; Safe.Validate(settings); check("unsupported language safely falls back to English", settings.Language == "en"); settings.AppExePath = null; store.Save(settings);
                foreach (string candidate in new[] { @"\\example.invalid\share\ChatGPT.exe", "ChatGPT.exe", Path.Combine(root, "wrong.exe"), Path.Combine(root, "ChatGPT.exe") + "\"" }) {
                    bool rejected = false; try { OfficialInstall.ValidateCandidatePath(candidate); } catch (IOException) { rejected = true; } check("reject an unsafe or wrong application selection", rejected);
                }
                string fixtureFamily = "OpenAI.Codex_abcdefghijklm"; int fixtureDetects = 0;
                var fixtureClient = new OfficialInstall { Exe = Path.Combine(root, "updated", "ChatGPT.exe"), Family = fixtureFamily };
                var stableClient = ClientResolutionPolicy.ResolveSaved("saved", value => fixtureClient, () => { fixtureDetects++; return fixtureClient; });
                check("valid saved client does not trigger repeated discovery", ReferenceEquals(stableClient, fixtureClient) && fixtureDetects == 0);
                var updatedClient = ClientResolutionPolicy.ResolveSaved("old", value => { throw new ClientInstallationChangedException(fixtureFamily); }, () => { fixtureDetects++; return fixtureClient; });
                check("a replaced official package recovers its verified current installation", ReferenceEquals(updatedClient, fixtureClient) && fixtureDetects == 1);
                bad = false; try { ClientResolutionPolicy.ResolveSaved("old", value => { throw new ClientInstallationChangedException(fixtureFamily); }, () => new OfficialInstall { Family = "Other.Vendor_abcdefghijklm" }); } catch (IOException) { bad = true; }
                check("update recovery rejects a different package family", bad);
                bad = false; try { ClientResolutionPolicy.ResolveSaved("old", value => { throw new IOException("signature failure fixture"); }, () => { fixtureDetects++; return fixtureClient; }); } catch (IOException) { bad = true; }
                check("signature or arbitrary manual-path errors cannot trigger fallback", bad && fixtureDetects == 1);
                bad = false; try { ClientResolutionPolicy.ResolveSaved("old", value => { throw new ClientInstallationChangedException(fixtureFamily); }, () => { throw new IOException("no installed package fixture"); }); } catch (IOException) { bad = true; }
                check("missing replacement client remains an explicit failure", bad);
                var remembered = new Settings { AppExePath = "old" }; int fixtureSaves = 0;
                check("resolved updated path is persisted for future launches", ClientResolutionPolicy.Remember(remembered, fixtureClient, state => { fixtureSaves++; }) && remembered.AppExePath == fixtureClient.Exe && fixtureSaves == 1);
                ClientResolutionPolicy.Remember(remembered, fixtureClient, state => { fixtureSaves++; });
                check("unchanged resolved paths do not repeat settings writes", fixtureSaves == 1);
                remembered.AppExePath = "old";
                check("path persistence failure does not prevent this session from using the verified client", !ClientResolutionPolicy.Remember(remembered, fixtureClient, state => { throw new UnauthorizedAccessException(); }) && remembered.AppExePath == fixtureClient.Exe);
                check("unchanged app paths do not gate unrelated preferences", !ClientResolutionPolicy.NeedsValidation(" old ", "OLD") && ClientResolutionPolicy.NeedsValidation("new", "old") && !ClientResolutionPolicy.NeedsValidation("", "old"));
                bool missingApp = false; try { OfficialInstall.ResolveCandidate(Path.Combine(root, "missing", "ChatGPT.exe")); } catch (IOException) { missingApp = true; }
                check("a missing explicit application fails instead of silently using another install", missingApp);
                bool invalidApp = false; try { OfficialInstall.ResolveCandidate("automatic"); } catch (IOException) { invalidApp = true; }
                check("nonempty arbitrary text is not treated as automatic path detection", invalidApp);
                settings.ShareLocalState = true; settings.SharedStateDirectory = root; store.Save(settings);
                check("shared data preference survives encrypted roundtrip", store.Load().ShareLocalState && store.Load().SharedStateDirectory == root && LocalWorkState.DirectoryFor(settings) == root);
                settings.ShareLocalState = false; store.Save(settings);
                var legacy = Safe.Json().Deserialize<Settings>("{\"Version\":2,\"SingleWindow\":true,\"CloseBackground\":true,\"ShareProjects\":true,\"Accounts\":[{\"Id\":\"Default\",\"Name\":\"Synthetic legacy account\"}]}");
                Safe.Validate(legacy);
                check("legacy settings retain accounts without restoring automatic closing", legacy.Accounts.Count == 1 && legacy.ShareProjects && !LaunchPolicy.MustClose(LaunchMode.Open));
                check("legacy settings keep shared working state disabled", !legacy.ShareLocalState && LocalWorkState.DirectoryFor(legacy) == null);
                var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "accounts.dat"))); check("metadata is not stored as plaintext", !text.Contains("Synthetic account"));
                bad = false; try { Safe.Validate(new Settings { Accounts = new List<Profile> { new Profile { Id = "../escape", Name = "Synthetic" } } }); } catch { bad = true; } check("reject profile path traversal", bad);
                var p = settings.Accounts[2]; store.Prepare(p); string expected = Path.Combine(root, "Profiles", p.Id, "desktop"); check("derive private profile path from generated identifier", store.DesktopFor(p) == expected);
                var privateProfile = settings.Accounts[3]; store.Prepare(privateProfile);
                check("disabled history sharing selects distinct state directories for arbitrary accounts", LocalWorkState.DirectoryForProfile(store, settings, p) == store.HomeFor(p) && LocalWorkState.DirectoryForProfile(store, settings, privateProfile) == store.HomeFor(privateProfile) && store.HomeFor(p) != store.HomeFor(privateProfile));
                check("default account keeps its own existing state configuration in private mode", LocalWorkState.DirectoryForProfile(store, settings, new Profile { Id = "Default" }) == null);
                var guarded = new Settings { ShareProjects = false, ShareLocalState = false };
                check("disabled project sharing returns before reading profiles or creating helpers", ProjectSync.Run(null, null, guarded, null).AddedCount == 0 && ProjectSidebarSync.Prepare(null, null, guarded, null) == null);
                LocalWorkState.ValidateSelection(store.HomeFor(p), null, store.HomeFor(p).ToUpperInvariant());
                check("private history accepts a matching configured location", true);
                bool conflict = false; try { LocalWorkState.ValidateSelection(store.HomeFor(p), root); } catch (IOException) { conflict = true; }
                check("private history refuses a configured common location", conflict);
                conflict = false; try { LocalWorkState.ValidateSelection(store.HomeFor(p), null, root); } catch (IOException) { conflict = true; }
                check("private history refuses a managed common location", conflict);
                var privateStart = new OfficialInstall { Exe = Path.Combine(root, "fixture-app.exe") }.CreateStart(store, p, settings);
                check("private GUI launch explicitly selects its own auth and state homes", privateStart.EnvironmentVariables["CODEX_HOME"] == store.HomeFor(p) && privateStart.EnvironmentVariables["CODEX_SQLITE_HOME"] == store.HomeFor(p));
                var sharedSettings = new Settings { ShareLocalState = true, SharedStateDirectory = root };
                var sharedStart = new OfficialInstall { Exe = Path.Combine(root, "fixture-app.exe") }.CreateStart(store, p, sharedSettings);
                check("shared GUI launch changes only the chosen state home", sharedStart.EnvironmentVariables["CODEX_HOME"] == store.HomeFor(p) && sharedStart.EnvironmentVariables["CODEX_SQLITE_HOME"] == root);
                check("switching from common to private cannot silently wake the old common window", !LocalWorkState.CanReuse(p.Id, root, store.HomeFor(p)));
                store.SaveSnapshot(p, new Snapshot { SchemaVersion = 2, Profile = p.Id, Email = "synthetic@example.invalid", PlanType = "Plus", UpdatedAtUtc = DateTime.UtcNow.ToString("o"), Buckets = new List<Bucket>() });
                store.SaveSnapshot(p, new Snapshot { SchemaVersion = 2, Profile = p.Id, Email = "synthetic@example.invalid", PlanType = "Pro", UpdatedAtUtc = DateTime.UtcNow.ToString("o"), Buckets = new List<Bucket>() });
                check("replace existing encrypted snapshot", store.Read(p).PlanType == "Pro");
                check("mask private email by default", Safe.Mask("synthetic@example.invalid") == "s•••@example.invalid");
                var start = new ProcessStartInfo(); start.EnvironmentVariables["CODEX_TEST_SECRET"] = "synthetic"; start.EnvironmentVariables["OPENAI_API_KEY"] = "synthetic"; Safe.CleanEnvironment(start, "synthetic-home");
                check("do not inherit auth environment", !start.EnvironmentVariables.ContainsKey("OPENAI_API_KEY") && !start.EnvironmentVariables.ContainsKey("CODEX_TEST_SECRET") && start.EnvironmentVariables["CODEX_HOME"] == "synthetic-home");
                start.EnvironmentVariables["CODEX_SQLITE_HOME"] = "untrusted-inherited-state"; Safe.CleanEnvironment(start, "independent-auth-home");
                check("private mode removes inherited shared state override", !start.EnvironmentVariables.ContainsKey("CODEX_SQLITE_HOME"));
                Safe.CleanEnvironment(start, "independent-auth-home", root);
                check("shared state override does not replace independent auth home", start.EnvironmentVariables["CODEX_HOME"] == "independent-auth-home" && start.EnvironmentVariables["CODEX_SQLITE_HOME"] == root);
                check("existing private window cannot masquerade as shared", !LocalWorkState.CanReuse("A", null, root));
                check("existing shared window cannot masquerade as private", !LocalWorkState.CanReuse("A", root, null));
                check("same shared mode can reuse the recorded account window", LocalWorkState.CanReuse("A", root, root.ToUpperInvariant()));
                check("default data source window can remain available", LocalWorkState.CanReuse("Default", null, root));
                check("changing shared state location requires a new account window", !LocalWorkState.CanReuse("A", root, root + "-different"));
                bad = false; try { Safe.Validate(new Settings { ShareLocalState = true }); } catch (InvalidDataException) { bad = true; }
                check("shared mode cannot be enabled without an explicit location", bad);
                bad = false; try { LocalWorkState.DirectoryFor(new Settings { ShareLocalState = true, SharedStateDirectory = Path.Combine(output, "missing-state") }); } catch (IOException) { bad = true; }
                check("unavailable shared state fails without creating a replacement", bad && !Directory.Exists(Path.Combine(output, "missing-state")));
                var bytes = File.ReadAllBytes(Path.Combine(root, "accounts.dat")); bytes[bytes.Length / 2] ^= 0x55; File.WriteAllBytes(Path.Combine(root, "accounts.dat"), bytes); bad = false; try { store.Load(); } catch { bad = true; }
                check("corrupt encrypted metadata fails without reset", bad && File.ReadAllBytes(Path.Combine(root, "accounts.dat")).SequenceEqual(bytes));
                // Validate boundary decisions with descriptors only. Never start or stop any process in self-tests.
                string image = Path.GetFullPath(Path.Combine(output, "Official", "ChatGPT.exe"));
                Func<int, int, long, string, AppProcess> identity = (id, session, created, path) => new AppProcess { Id = id, SessionId = session, Started = created, Image = path };
                var expectedProcess = identity(12345, 8, 1234567890, image); var allowed = new[] { image };
                check("no client means no launch choice is needed", !LaunchPolicy.NeedsChoice(0));
                check("background-only clients still require a choice", LaunchPolicy.NeedsChoice(1));
                check("cancel never opens or closes clients", !LaunchPolicy.MayOpen(LaunchMode.Cancel, 0) && !LaunchPolicy.MayOpen(LaunchMode.Cancel, 3) && !LaunchPolicy.MustClose(LaunchMode.Cancel));
                check("parallel never requests closing existing clients", !LaunchPolicy.MustClose(LaunchMode.Parallel) && LaunchPolicy.MayOpen(LaunchMode.Parallel, 3));
                check("opening without a choice requires no existing clients", LaunchPolicy.MayOpen(LaunchMode.Open, 0) && !LaunchPolicy.MayOpen(LaunchMode.Open, 3) && !LaunchPolicy.MustClose(LaunchMode.Open));
                check("close mode requires verified empty inventory before opening", LaunchPolicy.MustClose(LaunchMode.CloseExisting) && LaunchPolicy.MayOpen(LaunchMode.CloseExisting, 0) && !LaunchPolicy.MayOpen(LaunchMode.CloseExisting, 3));
                check("invalid launch modes cannot open or close clients", !LaunchPolicy.MustClose((LaunchMode)99) && !LaunchPolicy.MayOpen((LaunchMode)99, 0));
                var summaryInstall = new OfficialInstall { Exe = image, Cli = Path.Combine(output, "codex.exe") };
                var grouped = new List<AppProcess> { identity(30001, 8, 1234567890, image) }; var ancestry = new Dictionary<int, int> { { 30001, 7 } };
                for (int child = 1; child <= 12; child++) { grouped.Add(identity(30001 + child, 8, 1234567890, child == 12 ? summaryInstall.Cli : image)); ancestry[30001 + child] = child == 12 ? 30002 : 30001; }
                var processSummary = ClientLifetime.Summarize(summaryInstall, grouped, ancestry);
                check("thirteen processes in one client tree describe one desktop instance", processSummary.DesktopInstances == 1 && processSummary.AuxiliaryProcesses == 12 && processSummary.BackgroundWorkers == 0 && !processSummary.Uncertain);
                grouped.Add(identity(30100, 8, 1234567890, image)); ancestry[30100] = 7;
                processSummary = ClientLifetime.Summarize(summaryInstall, grouped, ancestry);
                check("independent desktop roots are counted separately", processSummary.DesktopInstances == 2 && processSummary.AuxiliaryProcesses == 12);
                grouped.Add(identity(30200, 8, 1234567890, summaryInstall.Cli)); ancestry[30200] = 8;
                grouped.Add(identity(30201, 8, 1234567890, summaryInstall.Cli)); ancestry[30201] = 30200;
                processSummary = ClientLifetime.Summarize(summaryInstall, grouped, ancestry);
                check("standalone workers and their children are separated from desktops", processSummary.DesktopInstances == 2 && processSummary.BackgroundWorkers == 1 && processSummary.AuxiliaryProcesses == 13);
                check("a failed ancestry snapshot does not invent instance counts", ClientLifetime.Summarize(summaryInstall, grouped, null).Uncertain);
                check("processes exiting during an ancestry snapshot use an uncertain summary", ClientLifetime.Summarize(summaryInstall, grouped, new Dictionary<int, int>()).Uncertain);
                check("an empty inventory describes no client without a snapshot", ClientLifetime.Summarize(summaryInstall, new AppProcess[0], null).Description == "没有正在运行的客户端");
                check("cyclic ancestry does not loop or invent instance counts", ClientLifetime.Summarize(summaryInstall, new[] { identity(30300, 8, 1234567890, image) }, new Dictionary<int, int> { { 30300, 30301 }, { 30301, 30300 } }).Uncertain);
                check("a rootless official process cycle is reported as uncertain", ClientLifetime.Summarize(summaryInstall, new[] { identity(30300, 8, 1234567890, image), identity(30301, 8, 1234567890, image) }, new Dictionary<int, int> { { 30300, 30301 }, { 30301, 30300 } }).Uncertain);
                check("accept matching process identity without touching a process", ClientLifetime.MatchesTarget(expectedProcess, identity(12345, 8, 1234567890, image), allowed, 99, 8));
                check("reject changed process creation time", !ClientLifetime.MatchesTarget(expectedProcess, identity(12345, 8, 1234567891, image), allowed, 99, 8));
                check("reject changed process identifier", !ClientLifetime.MatchesTarget(expectedProcess, identity(12346, 8, 1234567890, image), allowed, 99, 8));
                check("reject another Windows session", !ClientLifetime.MatchesTarget(expectedProcess, identity(12345, 9, 1234567890, image), allowed, 99, 8));
                check("reject matching filename outside official path", !ClientLifetime.MatchesTarget(expectedProcess, identity(12345, 8, 1234567890, Path.Combine(output, "DifferentInstall", "ChatGPT.exe")), allowed, 99, 8));
                check("reject executable outside exact allowlist", !ClientLifetime.MatchesTarget(expectedProcess, expectedProcess, new[] { Path.Combine(output, "NotAllowed.exe") }, 99, 8));
                check("reject the switcher's own process", !ClientLifetime.MatchesTarget(expectedProcess, expectedProcess, allowed, 12345, 8));
                check("accept a ready browser window including a frameless one", ClientWindows.IsApplicationWindow(ClientWindowKind.OfficialBrowser, "Chrome_WidgetWin_1", 7, 0, 0, 800, 600, false));
                check("reject a titleless browser helper during startup", !ClientWindows.IsApplicationWindow(ClientWindowKind.OfficialBrowser, "Chrome_WidgetWin_1", 0, 0x00c40000, 0, 1100, 600, false));
                check("reject a generic native helper despite its size and caption", !ClientWindows.IsApplicationWindow(ClientWindowKind.OfficialBrowser, "NativeHelper", 12, 0x00c40000, 0, 1100, 600, false));
                check("reject a console window during account waking", !ClientWindows.IsApplicationWindow(ClientWindowKind.OfficialBrowser, "ConsoleWindowClass", 12, 0x00c40000, 0, 1100, 600, false));
                check("reject Chromium background widget windows", !ClientWindows.IsApplicationWindow(ClientWindowKind.OfficialBrowser, "Chrome_WidgetWin_0", 12, 0x00c40000, 0, 1100, 600, false));
                check("reject tool windows even with the browser class", !ClientWindows.IsApplicationWindow(ClientWindowKind.OfficialBrowser, "Chrome_WidgetWin_1", 12, 0x00c40000, 0x80, 800, 600, false));
                check("reject non-activating browser windows", !ClientWindows.IsApplicationWindow(ClientWindowKind.OfficialBrowser, "Chrome_WidgetWin_1", 12, 0x00c40000, 0x08000000, 800, 600, false));
                check("reject child browser controls", !ClientWindows.IsApplicationWindow(ClientWindowKind.OfficialBrowser, "Chrome_WidgetWin_1", 12, 0x40000000, 0, 800, 600, false));
                check("keep minimized browser windows eligible for restoration", ClientWindows.IsApplicationWindow(ClientWindowKind.OfficialBrowser, "Chrome_WidgetWin_1", 12, 0, 0, 160, 28, true));
                check("a hidden official native shell requires application reactivation", !ClientWindows.CanRestoreDirect(ClientWindowKind.OfficialBrowser, false, false, true));
                check("visible and minimized official windows can be restored directly", ClientWindows.CanRestoreDirect(ClientWindowKind.OfficialBrowser, true, false, true) && ClientWindows.CanRestoreDirect(ClientWindowKind.OfficialBrowser, true, true, true));
                check("a disabled official window is not reported as usable", !ClientWindows.CanRestoreDirect(ClientWindowKind.OfficialBrowser, true, false, false));
                check("controller hiding does not change official-window restoration policy", ClientWindows.CanRestoreDirect(ClientWindowKind.OwnController, false, false, true) && !ClientWindows.CanRestoreDirect((ClientWindowKind)99, true, false, true));
                var fixtureInstall = new OfficialInstall { Exe = System.Reflection.Assembly.GetExecutingAssembly().Location };
                var fixtureStart = fixtureInstall.CreateStart(store, p, settings);
                check("official launch suppresses console creation without hiding the GUI", fixtureStart.CreateNoWindow && !fixtureStart.UseShellExecute && fixtureStart.WindowStyle == ProcessWindowStyle.Normal);
                fixtureStart.Arguments = "--console-window-probe"; fixtureStart.RedirectStandardOutput = fixtureStart.RedirectStandardError = true;
                using (var fixtureProcess = Process.Start(fixtureStart)) {
                    var consoleRead = fixtureProcess.StandardOutput.ReadToEndAsync();
                    bool finished = fixtureProcess.WaitForExit(5000);
                    if (!finished) { fixtureProcess.Kill(); fixtureProcess.WaitForExit(1000); }
                    string consoleResult = consoleRead.Wait(1000) ? consoleRead.Result : "";
                    check("owned console fixture really starts without a console window", finished && fixtureProcess.ExitCode == 0 && consoleResult.Trim() == "no-console");
                }
                int launches = 0; var order = new List<string>();
                check("successful cleanup immediately launches on the same call", SwitchSequence.CloseThenOpen(() => { order.Add("closed"); return true; }, () => { order.Add("opened"); launches++; }) && launches == 1 && order.SequenceEqual(new[] { "closed", "opened" }));
                check("incomplete cleanup never launches", !SwitchSequence.CloseThenOpen(() => false, () => launches++) && launches == 1);
                bad = false; try { SwitchSequence.CloseThenOpen(() => { throw new IOException(); }, () => launches++); } catch (IOException) { bad = true; }
                check("cleanup failure cannot fall through to launch", bad && launches == 1);
                check("window waking verifies the exact recorded owner", ClientWindows.SameOwner(expectedProcess, identity(12345, 8, 1234567890, image), 8));
                check("window waking rejects PID reuse", !ClientWindows.SameOwner(expectedProcess, identity(12345, 8, 1234567891, image), 8));
                check("window waking rejects another session", !ClientWindows.SameOwner(expectedProcess, identity(12345, 9, 1234567890, image), 8));
                check("window waking rejects a different executable", !ClientWindows.SameOwner(expectedProcess, identity(12345, 8, 1234567890, Path.Combine(output, "Other.exe")), 8));
                check("window waking rejects missing owner identity", !ClientWindows.SameOwner(null, expectedProcess, 8));
                string outside = Path.Combine(Path.GetFullPath(output), "outside-metadata.dat");
                bad = false; try { store.Encode(outside, new { Synthetic = true }); } catch (InvalidDataException) { bad = true; }
                check("reject metadata writes outside private directory", bad && !File.Exists(outside));
                bad = false; try { store.Decode<Settings>(outside); } catch (InvalidDataException) { bad = true; }
                check("reject metadata reads outside private directory", bad);
                string sibling = Path.Combine(root + "-sibling", "metadata.dat"); bad = false;
                try { store.Encode(sibling, new { Synthetic = true }); } catch (InvalidDataException) { bad = true; }
                check("reject sibling directory sharing private path prefix", bad && !Directory.Exists(root + "-sibling"));
                string first = Path.Combine(output, "Project-One"), second = Path.Combine(output, "Project-Two");
                check("project deduplication ignores Windows case and folder order", ProjectSync.Key(new[] { first, second }) == ProjectSync.Key(new[] { second.ToUpperInvariant(), first + "\\" }));
                string wire = Safe.Wire(new { name = "中文项目 🧪", path = "C:\\虚构目录\\项目" });
                check("Unicode project names survive ASCII-safe JSON transport", wire.All(c => c < 128) && (string)Safe.Field(Safe.Json().DeserializeObject(wire), "name") == "中文项目 🧪");
                var sourceSidebar = Safe.Map(Safe.Json().DeserializeObject(Safe.Wire(new {
                    @auth = "source-only-fictional-auth", selected = "source-only-selection",
                    @local = true
                })));
                Func<string, string, string, object> projectRecord = (id, name, folder) => new { id = id, name = name, rootPaths = new[] { folder }, createdAt = 1, updatedAt = 2 };
                sourceSidebar["local-projects"] = new Dictionary<string, object> {
                    { "project-one", Safe.Json().DeserializeObject(Safe.Wire(projectRecord("project-one", "虚构项目一", first))) },
                    { "project-two", Safe.Json().DeserializeObject(Safe.Wire(projectRecord("project-two", "Fictional two", second))) },
                    { "g-p-cloud", Safe.Json().DeserializeObject(Safe.Wire(projectRecord("g-p-cloud", "Cloud must remain private", first))) }
                };
                sourceSidebar["project-order"] = new object[] { "project-two", "project-one", "g-p-cloud" };
                sourceSidebar["thread-project-assignments"] = new Dictionary<string, object> {
                    { "shared-thread", new Dictionary<string, object> { { "projectKind", "local" }, { "projectId", "project-one" } } },
                    { "cloud-thread", new Dictionary<string, object> { { "projectKind", "chatgpt" }, { "projectId", "g-p-cloud" } } },
                    { "remote-thread", new Dictionary<string, object> { { "projectKind", "local" }, { "projectId", "project-one" } } }
                };
                sourceSidebar["thread-project-membership-host-ids"] = new Dictionary<string, object> { { "remote-thread", "remote:fictional" } };
                sourceSidebar["sidebar-project-thread-orders"] = new Dictionary<string, object> { { "project-one", new Dictionary<string, object> { { "threadIds", new object[] { "shared-thread" } } } } };
                string sidebarHome = store.HomeFor(p);
                var targetSidebar = new Dictionary<string, object> {
                    { "auth", "target-only-fictional-auth" }, { "selected-project", new Dictionary<string, object> { { "type", "local" }, { "projectId", "old-alias" } } },
                    { "local-projects", new Dictionary<string, object> { { "old-alias", Safe.Json().DeserializeObject(Safe.Wire(projectRecord("old-alias", "Old alias", first))) }, { "g-p-target", new { account = "target-only" } } } },
                    { "thread-project-assignments", new Dictionary<string, object> { { "existing-thread", new Dictionary<string, object> { { "projectKind", "local" }, { "projectId", "old-alias" } } } } },
                    { "app-server-project-id-by-legacy-project-id-by-host", new Dictionary<string, object> { { "remote:fictional", new Dictionary<string, object> { { "remote-project", "remote-only-server" } } } } }
                };
                var sidebarPlan = ProjectSidebarSync.Build(new[] { sourceSidebar, targetSidebar }, new[] {
                    new SharedProject { ServerId = "server-one", Key = ProjectSync.Key(new[] { first }) }, new SharedProject { ServerId = "server-two", Key = ProjectSync.Key(new[] { second }) }
                });
                var mergedSidebar = ProjectSidebarSync.Merge(sidebarPlan, targetSidebar, sidebarHome);
                check("sidebar transfers local project records rather than only database entries", Safe.Map(mergedSidebar["local-projects"]).ContainsKey("project-one") && Safe.Map(mergedSidebar["local-projects"]).ContainsKey("project-two"));
                check("sidebar excludes cloud projects and preserves target cloud state", !Safe.Map(mergedSidebar["local-projects"]).ContainsKey("g-p-cloud") && Safe.Map(mergedSidebar["local-projects"]).ContainsKey("g-p-target"));
                check("sidebar does not copy source auth or unrelated account fields", (string)mergedSidebar["auth"] == "target-only-fictional-auth" && !mergedSidebar.ContainsKey("selected") && !mergedSidebar.ContainsKey("local"));
                check("sidebar folder aliases do not create duplicate projects", !Safe.Map(mergedSidebar["local-projects"]).ContainsKey("old-alias") && sidebarPlan.Aliases["old-alias"] == "project-one");
                check("sidebar preserves the source project order", ((string[])mergedSidebar["project-order"]).Take(2).SequenceEqual(new[] { "project-two", "project-one" }));
                check("sidebar maps common database IDs to the target's own home", (string)Safe.Field(Safe.Field(mergedSidebar["app-server-project-id-by-legacy-project-id-by-host"], "local:" + sidebarHome), "project-one") == "server-one");
                check("sidebar preserves unrelated remote host mappings", Safe.Map(mergedSidebar["app-server-project-id-by-legacy-project-id-by-host"]).ContainsKey("remote:fictional"));
                check("sidebar transfers local membership without cloud or remote assignments", (string)Safe.Field(Safe.Field(mergedSidebar["thread-project-assignments"], "shared-thread"), "projectId") == "project-one" && !Safe.Map(mergedSidebar["thread-project-assignments"]).ContainsKey("cloud-thread") && !Safe.Map(mergedSidebar["thread-project-assignments"]).ContainsKey("remote-thread"));
                check("sidebar remaps existing thread and selected project aliases", (string)Safe.Field(Safe.Field(mergedSidebar["thread-project-assignments"], "existing-thread"), "projectId") == "project-one" && (string)Safe.Field(mergedSidebar["selected-project"], "projectId") == "project-one");
                check("sidebar transfers the manual per-project thread order", Safe.Map(mergedSidebar["sidebar-project-thread-orders"]).ContainsKey("project-one"));
                check("sidebar ignores project IDs absent from the common database", ProjectSidebarSync.Build(new[] { sourceSidebar }, new SharedProject[0]).Projects.Count == 0);
                var targetOnly = ProjectSidebarSync.Merge(sidebarPlan, new Dictionary<string, object>(), sidebarHome);
                check("sidebar initializes a fresh profile without copying source state", targetOnly.ContainsKey("local-projects") && !targetOnly.ContainsKey("auth"));
                string sidebarFile = Path.Combine(sidebarHome, ProjectSidebarSync.StateFile); File.WriteAllText(sidebarFile, Safe.Json().Serialize(targetSidebar), new UTF8Encoding(false)); byte[] sidebarBefore = File.ReadAllBytes(sidebarFile);
                var deferredSidebar = ProjectSidebarSync.Apply(store, p, sidebarPlan, () => false);
                check("sidebar never modifies a running or unverified target", deferredSidebar.Deferred && !deferredSidebar.Changed && sidebarBefore.SequenceEqual(File.ReadAllBytes(sidebarFile)));
                var updatedSidebar = ProjectSidebarSync.Apply(store, p, sidebarPlan, () => true);
                var sidebarBackups = Directory.GetFiles(Path.Combine(root, "Backups", "Sidebar"), "*.dat");
                byte[] restoredSidebar = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(sidebarBackups.Single()), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                check("sidebar creates an encrypted byte-exact backup before replacement", updatedSidebar.Changed && restoredSidebar.SequenceEqual(sidebarBefore) && !Encoding.UTF8.GetString(File.ReadAllBytes(sidebarBackups.Single())).Contains("target-only-fictional-auth"));
                var repeatedSidebar = ProjectSidebarSync.Apply(store, p, sidebarPlan, () => true);
                check("sidebar sync is idempotent and does not create repeated backups", !repeatedSidebar.Changed && !repeatedSidebar.Deferred && Directory.GetFiles(Path.Combine(root, "Backups", "Sidebar"), "*.dat").Length == 1);
                int guardCalls = 0; File.WriteAllBytes(sidebarFile, sidebarBefore);
                var racedSidebar = ProjectSidebarSync.Apply(store, p, sidebarPlan, () => { guardCalls++; if (guardCalls == 3) File.WriteAllText(sidebarFile, "{\"changedElsewhere\":true}"); return true; });
                check("sidebar detects a concurrent state change without overwriting it", racedSidebar.Deferred && File.ReadAllText(sidebarFile).Contains("changedElsewhere"));
                var otherRoot = identity(22001, 8, 1234567890, image); otherRoot.ProfileId = "Default";
                check("sidebar can update a closed profile while another verified profile runs", ClientLifetime.AccountedFor(new[] { otherRoot, identity(22002, 8, 1234567891, image) }, new[] { otherRoot }, "A", new Dictionary<int, int> { { 22002, 22001 } }));
                check("sidebar refuses a live target even if its window is hidden", !ClientLifetime.AccountedFor(new[] { otherRoot }, new[] { otherRoot }, "Default", new Dictionary<int, int>()));
                check("sidebar refuses an untracked official background process", !ClientLifetime.AccountedFor(new[] { identity(22003, 8, 1234567891, image) }, new[] { otherRoot }, "A", new Dictionary<int, int>()));
                // Exercise only this development checker's own fictional window. No official client is targeted.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var freshnessNow = new DateTimeOffset(2000, 1, 2, 0, 0, 0, TimeSpan.Zero);
                check("empty, malformed and zone-less quota times cannot fabricate freshness", !QuotaFreshness.Latest(new[] { null, new Snapshot { UpdatedAtUtc = "invalid" }, new Snapshot { UpdatedAtUtc = "2000-01-01T16:00:00" } }, freshnessNow).HasValue);
                var earlierQuota = new Snapshot { UpdatedAtUtc = "2000-01-01T14:00:00Z" };
                var laterQuota = new Snapshot { UpdatedAtUtc = "2000-01-01T15:30:00.0000000+01:00" };
                check("latest quota time compares instants rather than strings or account order", QuotaFreshness.Latest(new[] { laterQuota, earlierQuota }, freshnessNow) == new DateTimeOffset(2000, 1, 1, 14, 30, 0, TimeSpan.Zero));
                check("future quota times do not override the last successful result", QuotaFreshness.Latest(new[] { earlierQuota, new Snapshot { UpdatedAtUtc = "2099-01-01T00:00:00Z" } }, freshnessNow) == new DateTimeOffset(2000, 1, 1, 14, 0, 0, TimeSpan.Zero));
                var freshnessStore = new PrivateStore(Path.Combine(output, "freshness-" + Guid.NewGuid().ToString("N")));
                var freshnessState = PreviewSettings(); Dashboard freshnessPanel = null; int freshnessQueries = 0; bool writeFreshness = false;
                Func<Profile, int, Snapshot> readFreshness = (profile, index) => freshnessStore.Read(profile);
                Func<System.Threading.CancellationToken, System.Threading.Tasks.Task> refreshFreshness = token => {
                    freshnessQueries++;
                    if (writeFreshness) freshnessStore.SaveSnapshot(freshnessState.Accounts[1], new Snapshot { Profile = freshnessState.Accounts[1].Id, Email = "", PlanType = "Plus", UpdatedAtUtc = "2000-01-01T14:30:00Z", Buckets = new List<Bucket>() });
                    freshnessPanel.Show(); return System.Threading.Tasks.Task.FromResult(0);
                };
                freshnessPanel = new Dashboard(null, freshnessState, true, null, refreshFreshness, null, readFreshness);
                Action pumpFreshness = () => app.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ApplicationIdle);
                try {
                    var stamp = (TextBlock)freshnessPanel.Window.FindName("LastRefresh"); freshnessPanel.Show(); pumpFreshness();
                    check("no saved successful query shows a dash rather than launch time", stamp.Text == new UiText("en").F("lastRefresh", "—") && (string)stamp.ToolTip == new UiText("en")["notRefreshed"]);
                    freshnessStore.SaveSnapshot(freshnessState.Accounts[0], new Snapshot { Profile = "Default", Email = "", PlanType = "Plus", UpdatedAtUtc = earlierQuota.UpdatedAtUtc, Buckets = new List<Bucket>() });
                    freshnessPanel.Show(); pumpFreshness();
                    check("a saved successful quota query appears with full date on hover", stamp.Text.EndsWith("14:00") && ((string)stamp.ToolTip).Contains("2000-01-01 14:00:00"));
                    writeFreshness = true; freshnessPanel.OpenFromExecutable(); pumpFreshness();
                    check("a successful newer account query advances the panel timestamp", freshnessQueries == 1 && stamp.Text.EndsWith("14:30"));
                    writeFreshness = false; freshnessPanel.OpenFromExecutable(); pumpFreshness();
                    check("a refresh without a new successful snapshot retains the timestamp", freshnessQueries == 2 && stamp.Text.EndsWith("14:30"));
                    freshnessPanel.Hide(); freshnessPanel.Show(); pumpFreshness();
                    check("tray reopen neither queries nor replaces the saved quota timestamp", freshnessQueries == 2 && stamp.Text.EndsWith("14:30"));
                    freshnessPanel.SetLanguage("zh-Hans"); pumpFreshness();
                    check("changing language translates freshness without changing its time", stamp.Text == "上次刷新 14:30" && freshnessQueries == 2);
                } finally { freshnessPanel.Window.Close(); }
                var restartedFreshness = new Dashboard(null, freshnessState, true, null, null, null, readFreshness);
                try { restartedFreshness.Show(); pumpFreshness(); check("a new panel recovers the timestamp from persisted encrypted snapshots", ((TextBlock)restartedFreshness.Window.FindName("LastRefresh")).Text == "上次刷新 14:30"); }
                finally { restartedFreshness.Window.Close(); }
                var ownWindow = new Window { Title = "Synthetic window lifecycle check", Width = 320, Height = 200, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Icon = UiTheme.WindowIcon() };
                Action pump = () => {
                    var frame = new DispatcherFrame(); app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
                };
                using (var self = Process.GetCurrentProcess()) {
                    var owner = identity(self.Id, self.SessionId, self.StartTime.ToUniversalTime().ToFileTimeUtc(), self.MainModule.FileName);
                    var wakeInstall = new OfficialInstall { Exe = self.MainModule.FileName };
                    check("unknown account wake returns without launching an instance", wakeInstall.WakeExisting(store, p, settings) == null);
                    owner.ProfileId = p.Id; owner.StateHome = store.HomeFor(p); store.Encode(Path.Combine(store.Root, "windows.dat"), new List<AppProcess> { owner });
                    var restored = wakeInstall.WakeExisting(store, p, settings);
                    check("a tracked running account with no recognized browser window returns without fallback launch", restored != null && restored.Reused && !restored.WindowFound);
                    check("old native-window records require an official reopen before direct restoration", !store.Decode<List<AppProcess>>(Path.Combine(store.Root, "windows.dat"))[0].LifecycleReady);
                    conflict = false; try { wakeInstall.WakeExisting(store, p, sharedSettings); } catch (AccountDataModeChangedException) { conflict = true; }
                    check("automatic wake refuses a tracked window with different sharing settings", conflict);
                    try {
                        ownWindow.Show(); pump(); ownWindow.Hide(); pump();
                        var taskbarImage = Path.Combine(root, "Fictional app", "ChatGPT.exe");
                        check("taskbar icon source points to the selected executable's icon resource", TaskbarPresentation.IconResource(taskbarImage) == Path.GetFullPath(taskbarImage) + ",0");
                        check("pinned independent windows open the selector without private arguments", TaskbarPresentation.SelectorCommand(image) == "\"" + Path.GetFullPath(image) + "\"");
                        check("independent taskbar identities are distinct without account names", TaskbarPresentation.IndependentId(p.Id) != TaskbarPresentation.IndependentId(privateProfile.Id));
                        var ownHandle = new System.Windows.Interop.WindowInteropHelper(ownWindow).Handle;
                        var applyPresentation = typeof(TaskbarPresentation).GetMethod("Apply", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                        check("Windows stores icon association on the fixture-owned window", (bool)applyPresentation.Invoke(null, new object[] { ownHandle, taskbarImage, p.Id, null, image }) && TaskbarPresentation.HasIconResource(ownHandle, taskbarImage));
                        check("repeating a taskbar association is idempotent", (bool)applyPresentation.Invoke(null, new object[] { ownHandle, taskbarImage, p.Id, null, image }) && TaskbarPresentation.HasIconResource(ownHandle, taskbarImage));
                        check("taskbar repair rejects a mismatched native owner", !TaskbarPresentation.Ensure(ownHandle, new AppProcess { Id = self.Id + 1, ProfileId = p.Id, Image = taskbarImage }));
                        check("read existing window icons without writing handles or launching a process", ClientWindows.RefreshIcons(owner, ClientWindowKind.OwnController));
                        bool woke = ClientWindows.Wake(owner, ClientWindowKind.OwnController); pump();
                        check("restore the checker's own hidden window", woke && ClientWindows.HasVisibleWindow(owner, ClientWindowKind.OwnController));
                        ownWindow.Show(); pump(); ownWindow.WindowState = WindowState.Minimized; pump();
                        bool minimized = ClientWindows.HasMinimizedWindow(owner, ClientWindowKind.OwnController); woke = ClientWindows.Wake(owner, ClientWindowKind.OwnController); pump();
                        check("restore the checker's own minimized window", minimized && woke && !ClientWindows.HasMinimizedWindow(owner, ClientWindowKind.OwnController));
                        var blank = new Window { Title = "", Width = 740, Height = 420, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Background = Brushes.Black };
                        try {
                            blank.Show(); pump(); var blankHandle = new System.Windows.Interop.WindowInteropHelper(blank).Handle;
                            var mainHandle = new System.Windows.Interop.WindowInteropHelper(ownWindow).Handle;
                            blank.Hide(); ownWindow.Title = ""; ownWindow.Hide(); pump();
                            // WPF substitutes an application title for an empty Title property.
                            // Set only the two fixture-owned native titles empty to model the startup helper.
                            NativeWindowCheck.SetWindowText(blankHandle, ""); NativeWindowCheck.SetWindowText(mainHandle, "");
                            bool titleless = NativeWindowCheck.GetWindowTextLength(mainHandle) == 0 && NativeWindowCheck.GetWindowTextLength(blankHandle) == 0;
                            woke = ClientWindows.Wake(owner, ClientWindowKind.OwnController); pump();
                            check("startup helper stays hidden until a titled application window is ready", titleless && !woke && !NativeWindowCheck.IsWindowVisible(mainHandle) && !NativeWindowCheck.IsWindowVisible(blankHandle));
                            ownWindow.Title = "Synthetic window lifecycle check"; woke = ClientWindows.Wake(owner, ClientWindowKind.OwnController); pump();
                            check("wake named window without showing a titleless hidden helper", woke && NativeWindowCheck.IsWindowVisible(mainHandle) && !NativeWindowCheck.IsWindowVisible(blankHandle));
                        } finally { blank.Close(); }
                        ClientWindows.RequestClose(owner); pump();
                        check("window cleanup cannot close its own controller", ClientWindows.HasVisibleWindow(owner, ClientWindowKind.OwnController));
                        var fixtureProcedure = new NativeWindowCheck.WindowProc(NativeWindowCheck.DefWindowProc);
                        var fixtureClass = new NativeWindowCheck.WindowClass { Name = "Chrome_WidgetWin_1", Procedure = fixtureProcedure };
                        ushort registered = NativeWindowCheck.RegisterClass(ref fixtureClass); IntPtr shellWindow = IntPtr.Zero;
                        try {
                            if (registered == 0) throw new IOException("Cannot create the owned browser-shell fixture.");
                            shellWindow = NativeWindowCheck.CreateWindowEx(0, fixtureClass.Name, "Fictional browser lifecycle", 0x00cf0000, 0, 0, 320, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            if (shellWindow == IntPtr.Zero) throw new IOException("Owned browser-shell fixture creation failed.");
                            check("close-to-tray hidden shell is never force-shown", !ClientWindows.Wake(owner) && !NativeWindowCheck.IsWindowVisible(shellWindow));
                            // Only the fixture's simulated official reopen makes its window visible.
                            NativeWindowCheck.ShowWindow(shellWindow, 5); pump();
                            check("officially reopened fixture is restored after becoming visible", ClientWindows.Wake(owner) && NativeWindowCheck.IsWindowVisible(shellWindow));
                            NativeWindowCheck.EnableWindow(shellWindow, false);
                            check("disabled browser fixture is not falsely reported ready", !ClientWindows.Wake(owner));
                            NativeWindowCheck.EnableWindow(shellWindow, true); NativeWindowCheck.ShowWindow(shellWindow, 0); pump();
                            check("repeated close-to-tray does not resurrect the native shell", !ClientWindows.Wake(owner) && !NativeWindowCheck.IsWindowVisible(shellWindow));
                        } finally { if (shellWindow != IntPtr.Zero) NativeWindowCheck.DestroyWindow(shellWindow); if (registered != 0) NativeWindowCheck.UnregisterClass(fixtureClass.Name, IntPtr.Zero); GC.KeepAlive(fixtureProcedure); }
                    } finally { ownWindow.Close(); }
                }
                int refreshRuns = 0, refreshCancels = 0; var pendingQuota = new System.Threading.Tasks.TaskCompletionSource<bool>();
                var quotaRunner = new QuotaRefreshRunner(async token => { refreshRuns++; using (token.Register(() => { refreshCancels++; pendingQuota.TrySetCanceled(); })) await pendingQuota.Task; });
                var firstRefresh = quotaRunner.Start(); var duplicateRefresh = quotaRunner.Start();
                check("simultaneous refresh requests coalesce into one running job", refreshRuns == 1 && quotaRunner.Running && ReferenceEquals(firstRefresh, duplicateRefresh));
                var testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) }; int timerTicks = 0;
                testTimer.Tick += delegate { timerTicks++; quotaRunner.Start(); }; testTimer.Start(); PumpUntil(() => timerTicks >= 3, 1500); testTimer.Stop();
                check("real timer ticks cannot create overlapping quota jobs", timerTicks >= 3 && refreshRuns == 1);
                var stopRefresh = quotaRunner.Stop(); PumpUntil(() => stopRefresh.IsCompleted, 1500);
                check("account operations can cancel and await quota work", stopRefresh.IsCompleted && firstRefresh.IsCanceled && refreshCancels == 1 && !quotaRunner.Running);
                pendingQuota = new System.Threading.Tasks.TaskCompletionSource<bool>(); var nextRefresh = quotaRunner.Start(); pendingQuota.SetResult(true); PumpUntil(() => nextRefresh.IsCompleted, 1500);
                check("quota refresh restarts after cancellation and releases its job state", nextRefresh.IsCompleted && !nextRefresh.IsCanceled && refreshRuns == 2 && !quotaRunner.Running);
                int executableQueries = 0; var launchQuery = new System.Threading.Tasks.TaskCompletionSource<bool>();
                int emptyQueries = 0; var emptyPanel = new Dashboard(null, new Settings(), true, null, token => { emptyQueries++; return System.Threading.Tasks.Task.FromResult(0); });
                try { emptyPanel.OpenFromExecutable(); pump(); check("an empty account list does not query a client or fabricate a refresh error", emptyQueries == 0 && ((TextBlock)emptyPanel.Window.FindName("Status")).Visibility == Visibility.Collapsed); }
                finally { emptyPanel.Window.Close(); }
                var refreshPanel = new Dashboard(null, PreviewSettings(), true, null, token => { executableQueries++; return launchQuery.Task; });
                try {
                    refreshPanel.Show(); pump(); check("showing the panel does not request quota work", executableQueries == 0);
                    refreshPanel.OpenFromExecutable(); PumpUntil(() => executableQueries == 1, 1500); check("starting the executable requests one quota refresh", executableQueries == 1);
                    refreshPanel.OpenFromExecutable(); refreshPanel.OpenFromExecutable(); pump(); check("repeated executable launches reuse an in-flight query", executableQueries == 1);
                    launchQuery.SetResult(true); pump();
                    refreshPanel.Hide(); refreshPanel.Show(); pump(); check("reopening from the tray does not request another quota refresh", executableQueries == 1);
                    refreshPanel.OpenFromExecutable(); pump(); check("a completed executable launch refreshes again without a 30-second display cooldown", executableQueries == 2);
                    Click((Button)refreshPanel.Window.FindName("RefreshAll")); pump(); check("manual refresh remains available", executableQueries == 3);
                    var refreshSettings = refreshPanel.CreateManagementDialog(); var refreshPage = refreshPanel.ShowPageAsync(refreshSettings); pump();
                    refreshPanel.OpenFromExecutable(); pump(); check("an executable launch preserves a pending settings form and defers quota work", executableQueries == 3 && refreshPanel.ActivePage == refreshSettings && !refreshPage.IsCompleted);
                    refreshSettings.Complete(false); pump(); check("only the deferred executable request refreshes after settings close", executableQueries == 4 && refreshPage.IsCompleted);
                    var ordinarySettings = refreshPanel.CreateManagementDialog(); var ordinaryPage = refreshPanel.ShowPageAsync(ordinarySettings); pump(); ordinarySettings.Complete(false); pump(); check("closing settings without an executable launch does not refresh", executableQueries == 4 && ordinaryPage.IsCompleted);
                    using (var trayImage = UiTheme.TrayIcon()) check("tray icon is loaded directly from a multi-size embedded icon", trayImage.Width == 32 && trayImage.Height == 32);
                    check("documentation renders at double resolution without changing the actual panel width", Snapshot(refreshPanel.Window).PixelWidth == (int)Math.Ceiling(refreshPanel.Window.ActualWidth * 2) && Math.Abs(refreshPanel.Window.Width - 350) < 1);
                } finally { refreshPanel.Window.Close(); }
                int backgroundQueries = 0;
                var backgroundPanel = new Dashboard(null, PreviewSettings(), true, null, token => { backgroundQueries++; return System.Threading.Tasks.Task.FromResult(0); });
                try {
                    backgroundPanel.StartFromWindows(); PumpUntil(() => backgroundQueries == 1, 1500);
                    check("Windows startup refreshes usage without showing the panel or an account window", backgroundQueries == 1 && !backgroundPanel.Window.IsVisible);
                    backgroundPanel.HandleTrayClick(System.Windows.Forms.MouseButtons.Left, new Point(600, 900)); pump();
                    check("opening the tray after background startup does not refresh again", backgroundPanel.Window.IsVisible && backgroundQueries == 1);
                } finally { backgroundPanel.Window.Close(); }
                using (var fixtureIcon = new System.Windows.Forms.NotifyIcon { Icon = UiTheme.TrayIcon(), Text = "Synthetic placement check", Visible = true }) {
                    IntPtr iconWindow; uint iconId;
                    check("the owned tray icon exposes a valid native identifier", TrayIconLocation.Identify(fixtureIcon, out iconWindow, out iconId) && iconWindow != IntPtr.Zero && iconId > 0);
                }
                var desktopArea = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                Point? iconPoint = new Point(desktopArea.Left + desktopArea.Width * .65, desktopArea.Bottom + 4);
                var locatedPanel = new Dashboard(null, PreviewSettings(), true, null, null, () => iconPoint);
                Func<NativeWindowCheck.Bounds> locatedBounds = () => { NativeWindowCheck.Bounds value; if (!NativeWindowCheck.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(locatedPanel.Window).Handle, out value)) throw new IOException("Owned panel bounds unavailable."); return value; };
                try {
                    locatedPanel.OpenFromExecutable(); pump(); var executableBounds = locatedBounds();
                    check("desktop executable opens the native panel next to the tray anchor", executableBounds.Top > desktopArea.Top && executableBounds.Bottom <= desktopArea.Bottom && Math.Abs((executableBounds.Left + executableBounds.Right) / 2.0 - iconPoint.Value.X) < 2);
                    locatedPanel.Window.Hide(); locatedPanel.HandleTrayClick(System.Windows.Forms.MouseButtons.Left, new Point(desktopArea.Left + 10, desktopArea.Top + 10)); pump(); var trayBounds = locatedBounds();
                    check("desktop and tray activation use the same actual icon position", executableBounds.Left == trayBounds.Left && executableBounds.Top == trayBounds.Top);
                    iconPoint = new Point(iconPoint.Value.X - 200, iconPoint.Value.Y); locatedPanel.OpenFromExecutable(); pump(); var movedBounds = locatedBounds();
                    check("relaunching the executable follows a moved tray icon", Math.Abs(movedBounds.Left - executableBounds.Left + 200) < 2);
                    iconPoint = null; locatedPanel.OpenFromExecutable(); pump(); var rememberedBounds = locatedBounds();
                    check("a temporarily unavailable icon retains the last tray position", rememberedBounds.Left == movedBounds.Left && rememberedBounds.Top == movedBounds.Top);
                    iconPoint = new Point(desktopArea.Left + desktopArea.Width * .65, desktopArea.Bottom + 4);
                    PumpUntil(() => locatedBounds().Left == executableBounds.Left, 1500);
                    check("a delayed tray registration corrects the initial fallback without reopening", locatedBounds().Left == executableBounds.Left);
                } finally { locatedPanel.Window.Close(); }
                check("a cold desktop launch without an icon uses the taskbar fallback", TrayIconLocation.Resolve(null, null, null, new Point(1400, 900)) == new Point(1400, 900));
                check("taskbar fallback supports top and vertical taskbars", TrayIconLocation.TaskbarAnchor(new Rect(0, 0, 1600, 40), 1).Y == 20 && TrayIconLocation.TaskbarAnchor(new Rect(-40, 0, 40, 1000), 0).X == -20);
                // Pages must reuse this panel's one HWND and restore nested form state.
                var pageState = PreviewSettings(); var pagePanel = new Dashboard(null, pageState, true);
                try {
                    pagePanel.Show(); pump(); int windowCount = app.Windows.Count; double panelWidth = pagePanel.Window.ActualWidth;
                    var accountRows = (StackPanel)pagePanel.Window.FindName("Cards");
                    check("account cards use one full-width action with no separate wake arrow", accountRows.Children.Count == 2 && accountRows.Children.OfType<Button>().Count() == 2 && !Descendants<Button>(accountRows).Any(button => (button.Content as string) == "↗"));
                    var work = new Rect(0, 0, 1600, 900); var size = new Size(350, 360);
                    var nearFirst = PanelPlacement.Position(work, new Point(400, 920), size, 8); var nearSecond = PanelPlacement.Position(work, new Point(1000, 920), size, 8);
                    check("panel horizontal placement follows the clicked tray position", nearSecond.X - nearFirst.X == 600 && nearFirst.Y == nearSecond.Y);
                    var atTop = PanelPlacement.Position(work, new Point(700, -20), size, 8);
                    check("a top-edge tray position places the panel below the icon", atTop.Y == 8 && atTop.X == 525);
                    var side = PanelPlacement.Position(work, new Point(0, 600), size, 8);
                    check("panel stays inside the work area beside a screen-edge tray", work.Contains(new Rect(side, size)) && side.X == 8);
                    var negativeMonitor = new Rect(-1920, 0, 1920, 1040); var negative = PanelPlacement.Position(negativeMonitor, new Point(-1200, 1080), size, 8);
                    check("panel placement supports monitors with negative screen coordinates", negativeMonitor.Contains(new Rect(negative, size)) && negative.X < 0);
                    var resized = PanelPlacement.Position(work, new Point(700, 920), new Size(350, 450), 8);
                    check("changing page height retains the same horizontal tray anchor", resized.X == PanelPlacement.Position(work, new Point(700, 920), size, 8).X);
                    var panelHandle = new System.Windows.Interop.WindowInteropHelper(pagePanel.Window).Handle;
                    LaunchMode selected = LaunchMode.Cancel;
                    var launchPage = pagePanel.CreateLaunchDialog(pageState.Accounts[1], PreviewProcesses(), mode => selected = mode);
                    var launchPending = pagePanel.ShowPageAsync(launchPage); pump();
                    check("launch choice replaces the panel content without creating a window", pagePanel.ActivePage == launchPage && app.Windows.Count == windowCount && pagePanel.Window.OwnedWindows.Count == 0 && ReferenceEquals(((ContentControl)pagePanel.Window.FindName("PageHost")).Content, launchPage.View));
                    // Windows may round the requested logical width to device pixels.
                    check("launch page keeps the accepted compact panel width and native handle", Math.Abs(pagePanel.Window.Width - 350) < 1 && Math.Abs(pagePanel.Window.ActualWidth - panelWidth) < 0.1 && new System.Windows.Interop.WindowInteropHelper(pagePanel.Window).Handle == panelHandle);
                    pagePanel.HandleTrayClick(System.Windows.Forms.MouseButtons.Right); pump();
                    check("right click hides the same pending panel without an overlay", !pagePanel.Window.IsVisible && pagePanel.ActivePage == launchPage && !launchPending.IsCompleted);
                    var pause = Stopwatch.StartNew(); PumpUntil(() => pause.ElapsedMilliseconds > 270, 400);
                    pagePanel.HandleTrayClick(System.Windows.Forms.MouseButtons.Left); pump();
                    check("left click reopens the same unanswered page", pagePanel.Window.IsVisible && pagePanel.ActivePage == launchPage && !launchPending.IsCompleted && selected == LaunchMode.Cancel);
                    pagePanel.Show(); pump();
                    check("reopening restores the same pending page", pagePanel.Window.IsVisible && pagePanel.ActivePage == launchPage && !launchPending.IsCompleted);
                    Click((Button)launchPage.Footer.Children[0]); pump();
                    check("launch cancel returns home without choosing either operation", launchPending.IsCompleted && !launchPending.Result && selected == LaunchMode.Cancel && pagePanel.ActivePage == null && ReferenceEquals(((ContentControl)pagePanel.Window.FindName("PageHost")).Content, pagePanel.Window.FindName("HomeView")));
                    var choose = pagePanel.CreateLaunchDialog(pageState.Accounts[1], PreviewProcesses(), mode => selected = mode);
                    var choosePending = pagePanel.ShowPageAsync(choose); pump(); Click(choose.Body.Children.OfType<Button>().First()); pump();
                    check("an inline launch choice returns the selected mode exactly once", choosePending.IsCompleted && choosePending.Result && selected == LaunchMode.Parallel && pagePanel.ActivePage == null);
                    var management = pagePanel.CreateManagementDialog(); var managementPending = pagePanel.ShowPageAsync(management); pump();
                    var projectOption = Descendants<CheckBox>(management.View).First(); projectOption.IsChecked = false;
                    var intervalPicker = Descendants<ComboBox>(management.View).Single(x => x.Name == "QuotaInterval"); intervalPicker.SelectedItem = intervalPicker.Items.Cast<LanguageChoice>().First(x => x.Id == "10");
                    var removeIcon = (System.Windows.Shapes.Path)Descendants<Button>(management.View).First(x => x.Name == "RemoveAccount").Content;
                    check("remove action uses a visible thick red vector icon", removeIcon.StrokeThickness >= 2 && removeIcon.Width >= 11);
                    check("settings scroll region has no dotted focus rectangle", !((ScrollViewer)management.View.Children[1]).Focusable && ((ScrollViewer)management.View.Children[1]).FocusVisualStyle == null);
                    var settingsScroll = (ScrollViewer)management.View.Children[1]; settingsScroll.ApplyTemplate();
                    check("scroll content is clipped to keep headers and save buttons visible", ((FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(settingsScroll, 0)).ClipToBounds && ((FrameworkElement)settingsScroll.Template.FindName("PART_ScrollContentPresenter", settingsScroll)).ClipToBounds);
                    Descendants<ComboBox>(management.View).Single(x => x.Name == "LanguagePicker").SelectedItem = UiText.Languages.First(x => x.Id == "ja");
                    Descendants<TextBox>(management.View).Single().Text = "unsaved fictional path";
                    check("management no longer includes startup status or settings", !Descendants<TextBlock>(management.View).Any(textBlock => textBlock.Text.Contains("开机启动")) && !Descendants<Button>(management.View).Any(button => (button.Content as string ?? "").Contains("启动设置")));
                    projectOption.Focus(); pump(); projectOption.ApplyTemplate();
                    var tick = (System.Windows.Shapes.Path)projectOption.Template.FindName("Tick", projectOption);
                    check("checkbox keyboard focus uses the custom ring rather than a dotted text border", projectOption.FocusVisualStyle == null && ((FrameworkElement)projectOption.Template.FindName("FocusRing", projectOption)).Visibility == Visibility.Visible);
                    check("checkbox glyph is centered inside a fixed-size selection box", tick.Width == 10 && tick.Height == 9 && tick.HorizontalAlignment == HorizontalAlignment.Center && tick.VerticalAlignment == VerticalAlignment.Center);
                    var renameButton = Descendants<Button>(management.View).First(button => button.Name == "RenameAccount"); Click(renameButton); pump();
                    var renamePage = pagePanel.ActivePage; var renameInput = Descendants<TextBox>(renamePage.View).Single();
                    check("nested rename stays inside the same native panel", renamePage != management && app.Windows.Count == windowCount && pagePanel.Window.OwnedWindows.Count == 0 && Math.Abs(pagePanel.Window.ActualWidth - panelWidth) < 0.1);
                    renameInput.Text = ""; pump(); check("inline naming rejects an empty name", !((Button)renamePage.Footer.Children[1]).IsEnabled);
                    renameInput.Text = "虚构改名账号"; Click((Button)renamePage.Footer.Children[1]); pump();
                    check("saving a nested name returns to management and preserves unsaved options", pagePanel.ActivePage == management && projectOption.IsChecked == false && pageState.Accounts[0].Name == "虚构改名账号" && !managementPending.IsCompleted);
                    var removeButton = Descendants<Button>(management.View).First(button => button.Name == "RemoveAccount"); Click(removeButton); pump();
                    var removePage = pagePanel.ActivePage; Click((Button)removePage.Footer.Children[0]); pump();
                    check("nested removal cancellation restores management without changing accounts", pagePanel.ActivePage == management && pageState.Accounts.Count == 2 && projectOption.IsChecked == false);
                    management.IsBusy = true; management.Complete(false); pump();
                    check("busy settings cannot be dismissed before their save finishes", !managementPending.IsCompleted && pagePanel.ActivePage == management);
                    management.IsBusy = false; Click((Button)management.Footer.Children[0]); pump();
                    check("settings cancellation returns home and preserves saved project options", managementPending.IsCompleted && !managementPending.Result && pagePanel.ActivePage == null && pageState.ShareProjects);
                    check("settings cancellation discards the edited language and path", pageState.Language == "en" && pageState.AppExePath == null && pageState.QuotaRefreshMinutes == 0);
                    TextBox input; var naming = pagePanel.CreateNameDialog("添加账号", "虚构账号", out input); var namingPending = pagePanel.ShowPageAsync(naming); pump();
                    Click(Descendants<Button>(naming.View).First(button => (button.Content as string) == "‹")); pump();
                    check("the inline back button cancels the page without closing the panel", namingPending.IsCompleted && !namingPending.Result && pagePanel.ActivePage == null && pagePanel.Window.IsVisible && app.Windows.Count == windowCount);
                } finally { pagePanel.Window.Close(); }
                var pathState = PreviewSettings(); string selectedApp = Path.Combine(root, "manual", "ChatGPT.exe"), detectedApp = Path.Combine(root, "detected", "ChatGPT.exe"); string lastCandidate = null; int resolverCalls = 0; bool fixtureClientAvailable = true;
                var pathPanel = new Dashboard(null, pathState, true, candidate => { resolverCalls++; lastCandidate = candidate; if (!fixtureClientAvailable || candidate == "invalid fixture") throw new IOException(); return new OfficialInstall { Exe = String.IsNullOrEmpty(candidate) || candidate == detectedApp ? detectedApp : selectedApp, Cli = Path.Combine(root, "resources", "codex.exe") }; });
                try {
                    pathPanel.Show(); var form = pathPanel.CreateManagementDialog(); var pending = pathPanel.ShowPageAsync(form); pump();
                    var location = Descendants<TextBox>(form.View).Single(); check("opening settings displays the auto-detected application path", location.Text == detectedApp && lastCandidate == "");
                    Click((Button)form.Footer.Children[1]); pump(); check("saving a detected path pins the actual executable for future opens", pending.IsCompleted && pending.Result && pathState.AppExePath == detectedApp);
                    int beforeSavedPage = resolverCalls; form = pathPanel.CreateManagementDialog(); pending = pathPanel.ShowPageAsync(form); pump(); location = Descendants<TextBox>(form.View).Single();
                    check("opening saved settings displays the path without repeating discovery", resolverCalls == beforeSavedPage && location.Text == detectedApp);
                    Click((Button)form.Footer.Children[1]); pump();
                    check("saving unchanged settings never revalidates the saved client", pending.IsCompleted && pending.Result && resolverCalls == beforeSavedPage);
                    form = pathPanel.CreateManagementDialog(); pending = pathPanel.ShowPageAsync(form); pump(); location = Descendants<TextBox>(form.View).Single();
                    location.Text = linkPath;
                    Click(Descendants<Button>(form.View).Single(x => x.Name == "CheckApp")); pump(); check("checking a manual shortcut displays the canonical executable", lastCandidate == linkPath && location.Text == selectedApp);
                    Click((Button)form.Footer.Children[1]); pump(); check("save persists the actual manual executable for future launches", pending.IsCompleted && pending.Result && pathState.AppExePath == selectedApp);
                    var manualStart = new OfficialInstall { Exe = pathState.AppExePath }.CreateStart(store, p, settings); check("launch information uses exactly the selected executable", manualStart.FileName == selectedApp && !manualStart.UseShellExecute && manualStart.Arguments.Contains("--user-data-dir="));
                    form = pathPanel.CreateManagementDialog(); pending = pathPanel.ShowPageAsync(form); pump(); location = Descendants<TextBox>(form.View).Single(); location.Text = "invalid fixture";
                    Click((Button)form.Footer.Children[1]); pump(); check("invalid manual path keeps settings open and retains the previous selection", !pending.IsCompleted && pathState.AppExePath == selectedApp && form.Error.Visibility == Visibility.Visible);
                    Click(Descendants<Button>(form.View).Single(x => x.Name == "DetectApp")); pump(); check("Auto detect really resolves and displays the current installation", lastCandidate == "" && location.Text == detectedApp);
                    Click((Button)form.Footer.Children[1]); pump(); check("explicit Auto detect replaces the saved path only after saving", pending.IsCompleted && pending.Result && pathState.AppExePath == detectedApp);
                    fixtureClientAvailable = false; beforeSavedPage = resolverCalls;
                    form = pathPanel.CreateManagementDialog(); pending = pathPanel.ShowPageAsync(form); pump();
                    var preferenceLanguage = Descendants<ComboBox>(form.View).Single(x => x.Name == "LanguagePicker"); preferenceLanguage.SelectedItem = UiText.Languages.First(x => x.Id == "zh-Hans");
                    var preferenceInterval = Descendants<ComboBox>(form.View).Single(x => x.Name == "QuotaInterval"); preferenceInterval.SelectedItem = preferenceInterval.Items.Cast<LanguageChoice>().First(x => x.Id == "30");
                    Click((Button)form.Footer.Children[1]); pump();
                    check("unavailable unchanged client cannot block saving language or refresh preferences", pending.IsCompleted && pending.Result && pathState.Language == "zh-Hans" && pathState.QuotaRefreshMinutes == 30 && resolverCalls == beforeSavedPage && pathState.AppExePath == detectedApp);
                    form = pathPanel.CreateManagementDialog(); pending = pathPanel.ShowPageAsync(form); pump(); location = Descendants<TextBox>(form.View).Single(); location.Text = "invalid fixture";
                    Descendants<ComboBox>(form.View).Single(x => x.Name == "LanguagePicker").SelectedItem = UiText.Languages.First(x => x.Id == "ja");
                    Click((Button)form.Footer.Children[1]); pump();
                    check("failed path changes explain the client problem in the selected language without changing saved preferences", !pending.IsCompleted && form.Error.Text == new UiText("ja")["clientSelectionFailed"] && pathState.Language == "zh-Hans" && pathState.AppExePath == detectedApp);
                    form.Complete(false);
                } finally { pathPanel.Window.Close(); }
                // Both tray buttons reuse one native panel and preserve pending forms.
                foreach (var choice in UiText.Languages) {
                    var localized = PreviewSettings(); localized.Language = choice.Id; var panel = new Dashboard(null, localized, true);
                    try {
                        var before = app.Windows.Count; panel.HandleTrayClick(System.Windows.Forms.MouseButtons.Right, new Point(700, 900)); pump();
                        check(choice.Id + " right click opens the compact shared panel", panel.Window.IsVisible && Math.Abs(panel.Window.Width - 350) < 1 && app.Windows.Count == before && panel.Window.ContextMenu == null);
                        var actionLabels = Descendants<Button>((StackPanel)panel.Window.FindName("Actions")).ToArray();
                        check(choice.Id + " actions use larger readable text and fit the panel", actionLabels.Length == 6 && actionLabels.All(button => button.FontSize >= 13 && button.DesiredSize.Width <= 320));
                        var publicLinks = ((TextBlock)panel.Window.FindName("ProjectLinks")).Inlines.OfType<System.Windows.Documents.Hyperlink>().ToArray();
                        check(choice.Id + " main panel exposes only project and feedback links", publicLinks.Length == 2 && publicLinks.All(link => link.NavigateUri.Scheme == "https" && link.NavigateUri.Host == "github.com" && link.NavigateUri.Query == ""));
                        var authorCredit = (TextBlock)panel.Window.FindName("AuthorCredit");
                        check(choice.Id + " author credit has its own unbroken non-clickable line", authorCredit.Text == new UiText(choice.Id).F("authorLabel", "git-buster") && authorCredit.TextWrapping == TextWrapping.NoWrap && !authorCredit.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any());
                        var freshnessLabel = (TextBlock)panel.Window.FindName("LastRefresh");
                        check(choice.Id + " freshness is localized, visible and aligned at the lower right", freshnessLabel.Text == new UiText(choice.Id).F("lastRefresh", "14:30") && freshnessLabel.ActualWidth > 20 && freshnessLabel.TranslatePoint(new Point(freshnessLabel.ActualWidth, 0), panel.Window).X > 310 && freshnessLabel.DesiredSize.Width <= freshnessLabel.ActualWidth + 1);
                        check(choice.Id + " panel has an explicit embedded icon", panel.Window.Icon != null && panel.Window.Icon.IsFrozen);
                        var hideButton = (Button)panel.Window.FindName("Hide");
                        var closeGlyph = (System.Windows.Shapes.Path)hideButton.Content;
                        var glyphCenter = closeGlyph.TranslatePoint(new Point(closeGlyph.ActualWidth / 2, closeGlyph.ActualHeight / 2), hideButton);
                        check(choice.Id + " close glyph is centered inside a square highlight", Math.Abs(hideButton.ActualWidth - hideButton.ActualHeight) < 0.1 && Math.Abs(glyphCenter.X - hideButton.ActualWidth / 2) < 0.6 && Math.Abs(glyphCenter.Y - hideButton.ActualHeight / 2) < 0.6);
                        var footerLinks = (TextBlock)panel.Window.FindName("ProjectLinks");
                        check(choice.Id + " footer text has space for descenders above the panel edge", authorCredit.ActualHeight >= authorCredit.FontSize + 4 && authorCredit.TranslatePoint(new Point(0, authorCredit.ActualHeight), panel.Window).Y <= panel.Window.ActualHeight - 12 && footerLinks.TranslatePoint(new Point(0, footerLinks.ActualHeight), panel.Window).Y <= panel.Window.ActualHeight - 12);
                        var message = (TextBlock)panel.Window.FindName("Status"); message.Text = "Updated 2 · cached 0 · unavailable 0"; message.Visibility = Visibility.Visible;
                        panel.SetLanguage(choice.Id); pump();
                        check(choice.Id + " language changes remove obsolete transient status", message.Visibility == Visibility.Collapsed && message.Text == "");
                        var home = (FrameworkElement)panel.Window.FindName("HomeView");
                        foreach (double height in new[] { 320.0, 380.0 }) {
                            home.Measure(new Size(314, height)); home.Arrange(new Rect(0, 0, 314, height));
                            var authorBottom = authorCredit.TranslatePoint(new Point(0, authorCredit.ActualHeight), home).Y;
                            var linksBottom = footerLinks.TranslatePoint(new Point(0, footerLinks.ActualHeight), home).Y;
                            var scroll = (ScrollViewer)panel.Window.FindName("AccountList");
                            check(choice.Id + " small available height preserves author and links: " + height, authorBottom <= height && linksBottom <= height && linksBottom > authorBottom && scroll.ActualHeight < 220);
                        }
                        panel.Window.InvalidateMeasure(); pump();
                        CheckBox sharingChoice; var historyPage = panel.CreateHistoryDialog(out sharingChoice); var historyPending = panel.ShowPageAsync(historyPage); pump();
                        check(choice.Id + " local organization requires a separate explicit confirmation", sharingChoice.IsChecked == true && Descendants<Button>(historyPage.View).Single(button => button.Name == "RunHistoryOrganization").IsDefault == false && panel.ActivePage == historyPage);
                        Click((Button)historyPage.Footer.Children[0]); pump();
                        check(choice.Id + " cancelling history organization returns without changing data mode", historyPending.IsCompleted && !historyPending.Result && localized.LocalHistoryLayout == 0 && panel.ActivePage == null);
                        panel.HandleTrayClick(System.Windows.Forms.MouseButtons.Left); pump(); check(choice.Id + " left click hides that same panel", !panel.Window.IsVisible);
                        var pause = Stopwatch.StartNew(); PumpUntil(() => pause.ElapsedMilliseconds > 270, 400);
                        panel.HandleTrayClick(System.Windows.Forms.MouseButtons.Right); pump(); check(choice.Id + " right click reopens without a context menu", panel.Window.IsVisible && panel.Window.ContextMenu == null);
                        var manage = panel.CreateManagementDialog(); var pending = panel.ShowPageAsync(manage); pump();
                        var pageClose = Descendants<Button>(manage.View).Single(button => button.Name == "PageClose");
                        var pageGlyph = (System.Windows.Shapes.Path)pageClose.Content; var pageCenter = pageGlyph.TranslatePoint(new Point(pageGlyph.ActualWidth / 2, pageGlyph.ActualHeight / 2), pageClose);
                        check(choice.Id + " child-page close uses the same centered highlight", Math.Abs(pageClose.ActualWidth - pageClose.ActualHeight) < 0.1 && Math.Abs(pageCenter.X - pageClose.ActualWidth / 2) < 0.6 && Math.Abs(pageCenter.Y - pageClose.ActualHeight / 2) < 0.6);
                        var combo = Descendants<ComboBox>(manage.View).Single(x => x.Name == "LanguagePicker"); check(choice.Id + " settings list exactly the five supported languages", combo.Items.Count == 5 && ((LanguageChoice)combo.SelectedItem).Id == choice.Id);
                        var quota = Descendants<ComboBox>(manage.View).Single(x => x.Name == "QuotaInterval"); check(choice.Id + " settings offer every refresh interval", quota.Items.Count == 6);
                        check(choice.Id + " offers an unchecked tray-only startup option in a fresh preview", Descendants<CheckBox>(manage.View).Single(x => x.Name == "StartWithWindows").IsChecked == false);
                        var supportLinks = Descendants<Button>(manage.View).Where(button => button.Name.StartsWith("Support")).ToArray();
                        check(choice.Id + " About author is plain text with only project and feedback buttons", supportLinks.Length == 2 && !supportLinks.Any(button => button.Name == "SupportAuthor") && supportLinks.Any(button => button.Name == "SupportRepository") && supportLinks.Any(button => button.Name == "SupportFeedback"));
                        quota.SelectedItem = quota.Items.Cast<LanguageChoice>().First(x => x.Id == "30");
                        // Save a different language through the actual settings handler, then verify new pages.
                        combo.SelectedItem = UiText.Languages.First(x => x.Id == "fr"); Click((Button)manage.Footer.Children[1]); pump();
                        check(choice.Id + " language selection takes effect after saving", pending.IsCompleted && pending.Result && localized.Language == "fr" && localized.QuotaRefreshMinutes == 30 && ((Button)panel.Window.FindName("Add")).Content.ToString().Contains(new UiText("fr")["add"]));
                    } finally { panel.Window.Close(); }
                    var recoveryUiSettings = StartupStorage.Fresh(); recoveryUiSettings.Language = choice.Id;
                    var recoveryUi = new Dashboard(null, recoveryUiSettings, true, storageProblem: "storagePermission");
                    try {
                        recoveryUi.Show(); pump();
                        check(choice.Id + " recovery mode opens the panel and leaves navigation available", recoveryUi.Window.IsVisible && ((Button)recoveryUi.Window.FindName("StorageNotice")).IsVisible && ((Button)recoveryUi.Window.FindName("Settings")).IsEnabled && ((Button)recoveryUi.Window.FindName("Exit")).IsEnabled);
                        var detailPage = recoveryUi.CreateStorageDialog(); var detailTask = recoveryUi.ShowPageAsync(detailPage); pump(); Click((Button)detailPage.Footer.Children[0]); pump();
                        check(choice.Id + " recovery explanation returns to the same usable panel", detailTask.IsCompleted && recoveryUi.ActivePage == null && recoveryUi.Window.IsVisible);
                    } finally { recoveryUi.Window.Close(); }
                }
            } catch (Exception error) { lines.Add("FAIL synthetic test " + error.GetType().Name); File.WriteAllText(Path.Combine(output, "diagnostic.txt"), error.ToString()); Environment.ExitCode = 1; }
            File.WriteAllLines(Path.Combine(output, "results.txt"), lines, new UTF8Encoding(false));
        }
    }
}
