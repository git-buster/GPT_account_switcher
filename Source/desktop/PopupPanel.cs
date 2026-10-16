using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DesktopAccounts {
    public static class TrayIconLocation {
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct Identifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
        [StructLayout(LayoutKind.Sequential)] struct AppBar { public uint Size; public IntPtr Window; public uint Callback, Edge; public NativeRect Bounds; public IntPtr Parameter; }
        [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref Identifier identifier, out NativeRect bounds);
        [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint message, ref AppBar data);
        public static bool Identify(Forms.NotifyIcon icon, out IntPtr window, out uint iconId) {
            window = IntPtr.Zero; iconId = 0;
            if (icon == null || !icon.Visible) return false;
            try {
                // Framework and current WinForms use different field names. Read only
                // this application's own icon identity; never enumerate other icons.
                var flags = BindingFlags.Instance | BindingFlags.NonPublic; var type = typeof(Forms.NotifyIcon);
                var id = type.GetField("id", flags) ?? type.GetField("_id", flags);
                var field = type.GetField("window", flags) ?? type.GetField("_window", flags);
                if (id == null || field == null) return false;
                var native = field.GetValue(icon) as Forms.NativeWindow;
                if (native == null || native.Handle == IntPtr.Zero) return false;
                window = native.Handle; iconId = Convert.ToUInt32(id.GetValue(icon)); return true;
            } catch { return false; }
        }
        public static Point? Get(Forms.NotifyIcon icon) {
            IntPtr window; uint iconId;
            if (!Identify(icon, out window, out iconId)) return null;
            try {
                var identity = new Identifier { Size = (uint)Marshal.SizeOf(typeof(Identifier)), Window = window, Id = iconId, Guid = Guid.Empty };
                NativeRect bounds;
                if (Shell_NotifyIconGetRect(ref identity, out bounds) != 0 || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return null;
                var rectangle = new System.Drawing.Rectangle(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
                if (!Forms.Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(rectangle))) return null;
                return new Point(bounds.Left + rectangle.Width / 2.0, bounds.Top + rectangle.Height / 2.0);
            } catch { return null; }
        }
        public static Point TaskbarAnchor(Rect bounds, uint edge) {
            return edge == 0 || edge == 2 ? new Point(bounds.Left + bounds.Width / 2, bounds.Bottom - Math.Min(24, bounds.Height / 2))
                : new Point(bounds.Right - Math.Min(24, bounds.Width / 2), bounds.Top + bounds.Height / 2);
        }
        public static Point Fallback() {
            var data = new AppBar { Size = (uint)Marshal.SizeOf(typeof(AppBar)) };
            if (SHAppBarMessage(5, ref data) != UIntPtr.Zero && data.Bounds.Right > data.Bounds.Left && data.Bounds.Bottom > data.Bounds.Top)
                return TaskbarAnchor(new Rect(data.Bounds.Left, data.Bounds.Top, data.Bounds.Right - data.Bounds.Left, data.Bounds.Bottom - data.Bounds.Top), data.Edge);
            var area = Forms.Screen.PrimaryScreen.WorkingArea; return new Point(area.Right - 24, area.Bottom);
        }
        public static Point Resolve(Point? icon, Point? clicked, Point? previous, Point fallback) { return icon ?? clicked ?? previous ?? fallback; }
    }
    public static class PanelPlacement {
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        public static Point Position(Rect work, Point anchor, Size size, double gap) {
            double left = anchor.X - size.Width / 2, top = anchor.Y - size.Height - gap;
            if (top < work.Top + gap) top = anchor.Y + gap;
            left = Math.Max(work.Left + gap, Math.Min(left, work.Right - size.Width - gap));
            top = Math.Max(work.Top + gap, Math.Min(top, work.Bottom - size.Height - gap));
            return new Point(left, top);
        }
        public static void Place(Window window, Point anchor) {
            var handle = new WindowInteropHelper(window).Handle; NativeRect bounds;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out bounds)) return;
            var work = Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y)).WorkingArea;
            var source = PresentationSource.FromVisual(window); double scale = source == null ? 1 : source.CompositionTarget.TransformToDevice.M11;
            var point = Position(new Rect(work.Left, work.Top, work.Width, work.Height), anchor, new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), 8 * scale);
            SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(point.X), (int)Math.Round(point.Y), 0, 0, 0x0015);
        }
    }
    public sealed class Dashboard {
        readonly PrivateStore store; readonly Settings settings; readonly bool demo; readonly Func<string, OfficialInstall> resolveSettings; readonly Func<Point?> trayLocation; readonly string storageProblem;
        readonly StackPanel cards; readonly TextBlock status, recent, count, lastRefresh;
        readonly Func<Profile, int, Snapshot> readSnapshot;
        readonly FrameworkElement actions; readonly Button refresh;
        readonly ContentControl pageHost; readonly FrameworkElement homeView;
        readonly List<DialogShell> pages = new List<DialogShell>();
        readonly Dictionary<string, DateTime> queried = new Dictionary<string, DateTime>();
        Forms.NotifyIcon tray; bool busy, exiting, pickerOpen; DateTime hiddenAt; Point? trayAnchor;
        readonly DispatcherTimer quotaTimer = new DispatcherTimer(DispatcherPriority.Background);
        readonly DispatcherTimer placementTimer = new DispatcherTimer(DispatcherPriority.Loaded); int placementAttempts;
        readonly QuotaRefreshRunner quotaRunner; readonly bool syntheticRefresh; bool executableRefreshPending;
        bool refreshing { get { return quotaRunner != null && quotaRunner.Running; } }
        UiText text; long statusVersion;
        public readonly Window Window;
        public Dashboard(PrivateStore storage, Settings state, bool preview, Func<string, OfficialInstall> pathResolver = null, Func<CancellationToken, Task> refreshWork = null, Func<Point?> trayPosition = null, Func<Profile, int, Snapshot> snapshotSource = null, string storageProblem = null) {
            if (!preview && (pathResolver != null || refreshWork != null || trayPosition != null || snapshotSource != null)) throw new ArgumentException("Test hooks are preview-only.");
            trayLocation = trayPosition ?? (() => TrayIconLocation.Get(tray));
            resolveSettings = pathResolver ?? (preview ? (Func<string, OfficialInstall>)(candidate => new OfficialInstall { Exe = String.IsNullOrEmpty(candidate) ? @"<example app folder>\ChatGPT.exe" : candidate, Cli = @"<example app folder>\resources\codex.exe" }) : candidate => String.IsNullOrWhiteSpace(candidate) ? OfficialInstall.AutoDetect() : OfficialInstall.ResolveCandidate(candidate));
            store = storage; settings = state; demo = preview; this.storageProblem = storageProblem; text = new UiText(settings.Language);
            readSnapshot = snapshotSource ?? ((profile, index) => demo ? Sample(profile, index) : store == null ? null : store.Read(profile));
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dashboard.xaml")) Window = (Window)XamlReader.Load(stream);
            if (preview) Window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Window.Icon = UiTheme.WindowIcon();
            Window.Resources.MergedDictionaries.Add(UiTheme.Resources());
            pageHost = (ContentControl)Window.FindName("PageHost"); homeView = (FrameworkElement)Window.FindName("HomeView");
            cards = (StackPanel)Window.FindName("Cards"); status = (TextBlock)Window.FindName("Status"); recent = (TextBlock)Window.FindName("Recent"); count = (TextBlock)Window.FindName("Count"); actions = (FrameworkElement)Window.FindName("Actions"); refresh = (Button)Window.FindName("RefreshAll");
            lastRefresh = (TextBlock)Window.FindName("LastRefresh");
            var hide = (Button)Window.FindName("Hide"); hide.Style = (Style)Window.FindResource("CloseButton"); hide.Content = UiTheme.CloseGlyph();
            syntheticRefresh = preview && refreshWork != null; quotaRunner = new QuotaRefreshRunner(refreshWork ?? RefreshCore);
            ((Button)Window.FindName("Add")).Click += async delegate { await Add(); };
            refresh.Click += async delegate { await RefreshAll(); };
            ((Button)Window.FindName("OpenCurrent")).Click += async delegate { await OpenCurrent(); };
            ((Button)Window.FindName("Settings")).Click += async delegate { await Manage(); };
            ((Button)Window.FindName("OrganizeHistory")).Click += async delegate { await OrganizeHistory(); };
            ((Button)Window.FindName("StorageNotice")).Click += async delegate { if (!busy && ActivePage == null) await ShowPageAsync(CreateStorageDialog()); };
            ((Button)Window.FindName("Exit")).Click += delegate { Exit(); };
            ((Button)Window.FindName("Hide")).Click += delegate { Hide(); };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { if (ActivePage != null) ActivePage.Complete(false); else Hide(); e.Handled = true; } };
            Window.SizeChanged += delegate { if (!demo && Window.IsVisible) Window.Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded); };
            if (!demo) {
                tray = new Forms.NotifyIcon { Icon = UiTheme.TrayIcon(), Visible = true, Text = text["panel"] };
                tray.MouseUp += delegate(object sender, Forms.MouseEventArgs e) { var button = e.Button; var point = Forms.Cursor.Position; Window.Dispatcher.BeginInvoke(new Action(() => HandleTrayClick(button, new Point(point.X, point.Y)))); };
                Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } };
                Window.Deactivated += delegate { if (Window.IsVisible && !pickerOpen && !HasOpenPicker()) Hide(); };
                Application.Current.Exit += delegate { if (tray != null) { var icon = tray.Icon; tray.Visible = false; tray.Dispose(); tray = null; if (icon != null) icon.Dispose(); } };
            }
            quotaTimer.Tick += async delegate { await RefreshAll(true); };
            placementTimer.Interval = TimeSpan.FromMilliseconds(100);
            placementTimer.Tick += delegate {
                if (!Window.IsVisible || ++placementAttempts >= 10) { placementTimer.Stop(); return; }
                var anchor = trayLocation(); if (!anchor.HasValue) return;
                trayAnchor = anchor; placementTimer.Stop(); Place();
            };
            Window.Closed += delegate { quotaTimer.Stop(); placementTimer.Stop(); quotaRunner.Cancel(); foreach (var page in pages.ToArray().Reverse()) page.Complete(false); };
            ApplyLanguage(); Render(); ConfigureQuotaTimer();
        }
        bool HasOpenPicker() { return ActivePage != null && FindCombos(ActivePage.View).Any(combo => combo.IsDropDownOpen); }
        static IEnumerable<ComboBox> FindCombos(DependencyObject root) {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) { var combo = child as ComboBox; if (combo != null) yield return combo; foreach (var nested in FindCombos(child)) yield return nested; }
        }
        public string Language { get { return text.Language; } }
        public void SetLanguage(string language) { settings.Language = UiText.Normalize(language); text = new UiText(settings.Language); Say(null); ApplyLanguage(); Render(); }
        void ApplyLanguage() {
            Window.Title = text["panel"]; Window.Language = XmlLanguage.GetLanguage(text.Language);
            Window.FontFamily = new FontFamily(text.Language == "ja" ? "Yu Gothic UI, Segoe UI" : text.Language == "zh-Hant" ? "Microsoft JhengHei UI, Segoe UI" : text.Language == "zh-Hans" ? "Microsoft YaHei UI, Segoe UI" : "Segoe UI, Microsoft YaHei UI");
            ((TextBlock)Window.FindName("LocalHint")).Text = text["local"]; ((TextBlock)Window.FindName("Footer")).Text = text.F("footerShort", "0.6.13"); ((TextBlock)Window.FindName("Footer")).ToolTip = text.F("footer", "0.6.13");
            ((TextBlock)Window.FindName("AuthorCredit")).Text = text.F("authorLabel", ProjectLinks.Author);
            var links = (TextBlock)Window.FindName("ProjectLinks"); links.Inlines.Clear();
            foreach (var kind in new[] { ProjectLink.Repository, ProjectLink.Feedback }) {
                if (links.Inlines.Count > 0) links.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
                string label = text[kind == ProjectLink.Repository ? "projectLink" : "feedbackLink"];
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(label)) { NavigateUri = new Uri(ProjectLinks.Address(kind)), ToolTip = ProjectLinks.Address(kind), Foreground = Color("#7890B2") };
                var target = kind; link.RequestNavigate += delegate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) { if (!demo) try { ProjectLinks.Open(target); } catch { Say(text["linkFailed"]); } e.Handled = true; }; links.Inlines.Add(link);
            }
            var names = new[] { "Add", "RefreshAll", "OpenCurrent", "OrganizeHistory", "Settings", "Exit" }; var keys = new[] { "add", "refresh", "openChatGPT", "organizeHistory", "settings", "exit" }; var icons = new[] { "＋", "↻", "↗", "⇄", "⚙", "⏻" };
            for (int i = 0; i < names.Length; i++) ((Button)Window.FindName(names[i])).Content = icons[i] + "  " + text[keys[i]];
            ((Button)Window.FindName("Hide")).ToolTip = text["hide"]; System.Windows.Automation.AutomationProperties.SetName((Button)Window.FindName("Hide"), text["hide"]); if (tray != null) tray.Text = text["panel"];
        }
        public void HandleTrayClick(Forms.MouseButtons button) { var point = Forms.Cursor.Position; HandleTrayClick(button, new Point(point.X, point.Y)); }
        public void HandleTrayClick(Forms.MouseButtons button, Point anchor) {
            if (exiting || button != Forms.MouseButtons.Left && button != Forms.MouseButtons.Right) return;
            trayAnchor = TrayIconLocation.Resolve(trayLocation(), anchor, trayAnchor, anchor); if (Window.IsVisible) Hide(); else if (DateTime.UtcNow - hiddenAt > TimeSpan.FromMilliseconds(250)) Show();
        }
        public DialogShell ActivePage { get { return pages.LastOrDefault(); } }
        public async Task<bool> ShowPageAsync(DialogShell shell) {
            if (shell == null || shell.Completion.IsCompleted || pages.Contains(shell)) throw new InvalidOperationException();
            pages.Add(shell); pageHost.Content = shell.View; Show();
            try { return await shell.Completion; }
            finally { pages.Remove(shell); pageHost.Content = ActivePage == null ? homeView : ActivePage.View; Render(); if (Window.IsVisible) { Window.UpdateLayout(); Place(); } ResumeExecutableRefresh(); }
        }
        public void Hide() { placementTimer.Stop(); Window.Hide(); hiddenAt = DateTime.UtcNow; }
        public void Show() { if (ActivePage == null) Render(); if (!demo && !trayAnchor.HasValue) trayAnchor = TrayIconLocation.Resolve(trayLocation(), null, null, TrayIconLocation.Fallback()); Window.Show(); Window.UpdateLayout(); Place(); Window.Activate(); }
        public void OpenFromExecutable() {
            var icon = trayLocation(); trayAnchor = TrayIconLocation.Resolve(icon, null, trayAnchor, TrayIconLocation.Fallback());
            Show(); if (!icon.HasValue) { placementAttempts = 0; placementTimer.Start(); }
            executableRefreshPending = true; ResumeExecutableRefresh();
        }
        public void StartFromWindows() { executableRefreshPending = true; ResumeExecutableRefresh(); }
        void Place() { if (!Window.IsVisible || !trayAnchor.HasValue) return; PanelPlacement.Place(Window, trayAnchor.Value); }
        async void Exit() { if (exiting) return; if (busy) { Show(); Say(text["waitToQuit"]); return; } exiting = true; quotaTimer.Stop(); await StopRefresh(); if (tray != null) tray.Visible = false; Application.Current.Shutdown(); }
        public void ExitFromInstaller() { Exit(); }
        void Say(string value) { statusVersion++; status.Text = value ?? ""; status.Visibility = String.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
        bool NeedStore() { if (store != null) return true; Say(text["storageNoWrites"]); return false; }
        bool HistoryPending() { return store != null && CompleteHistorySharing.Pending(store); }
        public DialogShell CreateStorageDialog() {
            var shell = Page(text["storageTitle"], text[storageProblem ?? "storageUnavailable"]);
            shell.Body.Children.Add(Label(text[store == null ? "storageNoWrites" : "storageRecovery"], 12, "#66748A"));
            shell.Body.Children.Add(Label(text["storageOriginalKept"], 11, "#8A96A9"));
            var done = Button(shell.View, "back", "PrimaryButton"); done.Click += delegate { shell.Complete(true); }; shell.Footer.Children.Add(done); return shell;
        }
        void SetBusy(bool value) { busy = value; actions.IsEnabled = cards.IsEnabled = !value; refresh.IsEnabled = !value && !refreshing; refresh.Content = "↻  " + text[value || refreshing ? "working" : "refresh"]; if (!value) ResumeExecutableRefresh(); }
        void ConfigureQuotaTimer() { quotaTimer.Stop(); int minutes = QuotaRefreshPolicy.Normalize(settings.QuotaRefreshMinutes); if (!demo && minutes > 0) { quotaTimer.Interval = TimeSpan.FromMinutes(minutes); quotaTimer.Start(); } }
        void ResumeExecutableRefresh() {
            if (!executableRefreshPending || exiting || demo && !syntheticRefresh) return;
            Window.Dispatcher.BeginInvoke(new Action(async () => {
                if (!executableRefreshPending || exiting) return;
                if (refreshing) { executableRefreshPending = false; return; }
                if (busy || ActivePage != null) return;
                executableRefreshPending = false; queried.Clear(); await RefreshAll(true);
            }), DispatcherPriority.ApplicationIdle);
        }
        Task StopRefresh() { return quotaRunner.Stop(); }
        static SolidColorBrush Color(string hex) { return UiTheme.Brush(hex); }
        static TextBlock Label(string value, double size, string color) { return UiTheme.Text(value, size, color); }
        Button Button(FrameworkElement scope, string key, string style) { return UiTheme.Button(scope, text[key], style); }
        DialogShell Page(string title, string subtitle) { return UiTheme.Page(title, subtitle, null, text); }
        string Duration(int? value) { int m = value ?? 0; return m == 10080 ? text["week"] : m > 0 && m % 1440 == 0 ? text.F("days", m / 1440) : m > 0 && m % 60 == 0 ? text.F("hours", m / 60) : m > 0 ? text.F("minutes", m) : text["usage"]; }
        string Quota(QuotaWindow quota) {
            string reset = demo ? text["demoReset"] : text["resetUnknown"];
            if (!demo && quota.ResetsAt.HasValue) try { var at = DateTimeOffset.FromUnixTimeSeconds(quota.ResetsAt.Value).LocalDateTime; reset = at <= DateTime.Now ? text["refreshDue"] : text.F("resetAt", at.ToString("MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)); } catch { }
            return text.F("quota", Duration(quota.WindowDurationMins), quota.RemainingPercent.HasValue ? Math.Round(quota.RemainingPercent.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) : text["unknown"], reset);
        }
        Snapshot Sample(Profile profile, int index) { return new Snapshot { Profile = profile.Id, Email = "", PlanType = index == 0 ? "Plus" : "Pro", UpdatedAtUtc = "2000-01-01T14:30:00Z", Buckets = new List<Bucket> { new Bucket { Primary = new QuotaWindow { RemainingPercent = 80 - index * 20, WindowDurationMins = 300 }, Secondary = new QuotaWindow { RemainingPercent = 65 - index * 10, WindowDurationMins = 10080 } } } }; }
        void Render() {
            var notice = (Button)Window.FindName("StorageNotice"); notice.Visibility = storageProblem == null ? Visibility.Collapsed : Visibility.Visible; notice.Content = Label(text["storageBanner"], 11, "#99663B");
            cards.Children.Clear(); count.Text = text.F("accountCount", settings.Accounts.Count); var last = settings.Accounts.FirstOrDefault(p => p.Id == settings.LastId); recent.Text = last == null ? text["selectAccount"] : text.F("recent", last.Name);
            var work = SystemParameters.WorkArea; Window.MaxHeight = Math.Max(270, work.Height - 24);
            var snapshots = new List<Snapshot>();
            for (int i = 0; i < settings.Accounts.Count; i++) {
                var profile = settings.Accounts[i]; var snapshot = readSnapshot(profile, i); snapshots.Add(snapshot); bool selected = profile.Id == settings.LastId;
                var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(21) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                var marker = Label(selected ? "●" : "○", 15, selected ? "#3678F6" : "#BBC0CA"); marker.Margin = new Thickness(0, 1, 0, 0); row.Children.Add(marker);
                var content = new StackPanel { Margin = new Thickness(5, 0, 0, 0) }; Grid.SetColumn(content, 1); row.Children.Add(content);
                string plan = snapshot == null ? text["pending"] : String.IsNullOrWhiteSpace(snapshot.PlanType) || snapshot.PlanType == "未知" ? text["unknown"] : snapshot.PlanType;
                var heading = new DockPanel(); var badge = new Border { Background = Color(selected ? "#DFEAFE" : "#F0F2F6"), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(8, 1, 0, 0), Child = Label(plan, 10.5, "#747D90"), VerticalAlignment = VerticalAlignment.Top }; DockPanel.SetDock(badge, Dock.Right); heading.Children.Add(badge);
                heading.Children.Add(new TextBlock { Text = profile.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Color("#20252E"), TextTrimming = TextTrimming.CharacterEllipsis }); content.Children.Add(heading);
                int shown = 0;
                if (snapshot != null) foreach (var bucket in snapshot.Buckets) {
                    if (snapshot.Buckets.Count > 1) { var group = Label(bucket.LimitName ?? bucket.LimitId ?? text["usage"], 10.5, "#8C93A1"); group.Margin = new Thickness(0, 3, 0, 0); content.Children.Add(group); }
                    foreach (var quota in new[] { bucket.Primary, bucket.Secondary }) if (quota != null) { var label = Label(Quota(quota), 11, quota.RemainingPercent < 15 ? "#B27543" : "#727B8A"); label.Margin = new Thickness(0, 3, 0, 0); content.Children.Add(label); shown++; }
                }
                if (shown == 0) { var empty = Label(text["usageHint"], 11, "#9097A3"); empty.Margin = new Thickness(0, 3, 0, 0); content.Children.Add(empty); }
                string details = snapshot == null || demo ? "" : "\n" + (snapshot.Email != null && snapshot.Email.Contains('@') ? Safe.Mask(snapshot.Email) : text["privateAccount"]) + "\n" + text.F("lastCheck", Safe.Date(snapshot.UpdatedAtUtc).HasValue ? Safe.Stamp(snapshot.UpdatedAtUtc) : text["unknown"]);
                var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(9, 8, 9, 8), Margin = new Thickness(0, 1, 0, 1), Background = selected ? Color("#EEF4FF") : Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = text.F("openHint", profile.Name) + details };
                button.Click += async delegate { await WakeAccount(profile); }; cards.Children.Add(button);
            }
            if (settings.Accounts.Count == 0) cards.Children.Add(new TextBlock { Text = text["noAccounts"], Foreground = Color("#8A91A0"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 18, 12, 18) });
            var latest = QuotaFreshness.Latest(snapshots, DateTimeOffset.UtcNow);
            var displayTime = latest.HasValue ? (demo ? latest.Value.UtcDateTime : latest.Value.LocalDateTime) : (DateTime?)null;
            lastRefresh.Text = text.F("lastRefresh", displayTime.HasValue ? displayTime.Value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "—");
            lastRefresh.ToolTip = displayTime.HasValue ? text.F("lastRefreshDetail", displayTime.Value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)) : text["notRefreshed"];
            System.Windows.Automation.AutomationProperties.SetName(lastRefresh, (string)lastRefresh.ToolTip);
        }
        public DialogShell CreateNameDialog(string title, string initial, out TextBox box) {
            var shell = Page(title, text["nameHint"]); shell.Body.Children.Add(Label(text["name"], 12, "#748196"));
            var input = new TextBox { Text = initial, MaxLength = 40, Margin = new Thickness(0, 7, 0, 9) }; shell.Body.Children.Add(input); box = input;
            shell.Body.Children.Add(Label(text["nameLocal"], 11, "#8B97A9"));
            var cancel = Button(shell.View, "cancel", "QuietButton"); cancel.Margin = new Thickness(0, 0, 8, 0); cancel.Click += delegate { shell.Complete(false); }; shell.Footer.Children.Add(cancel);
            var save = Button(shell.View, "saveName", "PrimaryButton"); save.IsDefault = true; shell.Footer.Children.Add(save);
            Func<bool> valid = () => !String.IsNullOrWhiteSpace(input.Text) && !input.Text.Any(Char.IsControl); input.TextChanged += delegate { save.IsEnabled = valid(); }; save.IsEnabled = valid(); save.Click += delegate { if (valid()) shell.Complete(true); };
            shell.View.Loaded += delegate { input.Focus(); input.SelectAll(); }; return shell;
        }
        async Task<string> AskName(string title, string initial) { TextBox input; var shell = CreateNameDialog(title, initial, out input); return await ShowPageAsync(shell) ? input.Text.Trim() : null; }
        async Task Add() {
            if (!demo && !NeedStore()) return;
            if (!demo && HistoryPending()) { Say(text["historyRecoveryNeeded"]); return; }
            if (demo || busy || ActivePage != null || settings.Accounts.Count >= 500) return; SetBusy(true); try { await StopRefresh(); } finally { SetBusy(false); } string name = await AskName(text["add"], text.F("accountName", settings.Accounts.Count + 1)); if (name == null) return;
            var profile = new Profile { Id = Guid.NewGuid().ToString("N"), Name = name };
            try { store.Prepare(profile); settings.Accounts.Add(profile); store.Save(settings); Render(); Say(text["added"]); } catch { settings.Accounts.Remove(profile); Say(text["addFailed"]); }
        }
        async Task Rename(Profile profile) { string name = await AskName(text["renameTitle"], profile.Name); if (name == null) return; string old = profile.Name; profile.Name = name; try { if (!demo && store != null) store.Save(settings); Render(); } catch { profile.Name = old; Say(text["nameFailed"]); } }
        public DialogShell CreateRemoveDialog(Profile profile) {
            var shell = Page(text["removeTitle"], text["removeHint"]); var question = Label(text.F("removeQuestion", profile.Name), 13, "#3A4658"); question.Margin = new Thickness(0, 0, 0, 12); shell.Body.Children.Add(question);
            shell.Body.Children.Add(UiTheme.Card(Label(text["removeKeepData"], 11, "#8490A2")));
            var cancel = Button(shell.View, "keepAccount", "QuietButton"); cancel.Margin = new Thickness(0, 0, 8, 0); cancel.Click += delegate { shell.Complete(false); }; shell.Footer.Children.Add(cancel);
            var remove = Button(shell.View, "remove", "DangerButton"); remove.Click += delegate { shell.Complete(true); }; shell.Footer.Children.Add(remove); return shell;
        }
        async Task Remove(Profile profile) { if (!await ShowPageAsync(CreateRemoveDialog(profile))) return; int index = settings.Accounts.IndexOf(profile); string last = settings.LastId; settings.Accounts.Remove(profile); try { if (!demo && store != null) store.Save(settings); Render(); } catch { settings.Accounts.Insert(index, profile); settings.LastId = last; Say(text["removeFailed"]); } }
        TextBlock Section(StackPanel pane, string key, string detail) {
            var row = new DockPanel { Margin = new Thickness(0, pane.Children.Count == 0 ? 0 : 17, 0, 9) }; TextBlock counter = null;
            if (detail != null) { counter = Label(detail, 10, "#9AA4B3"); DockPanel.SetDock(counter, Dock.Right); row.Children.Add(counter); }
            var label = Label(text[key], 12, "#66748A"); label.FontWeight = FontWeights.SemiBold; row.Children.Add(label); pane.Children.Add(row); return counter;
        }
        sealed class PickerOwner : Forms.IWin32Window { public IntPtr Handle { get; private set; } public PickerOwner(IntPtr handle) { Handle = handle; } }
        public DialogShell CreateManagementDialog() {
            var shell = Page(text["settings"], text["settingsHint"]); var pane = shell.Body;
            var accountCount = Section(pane, "accounts", text.F("accountCount", settings.Accounts.Count)); var rows = new StackPanel();
            Action rebuild = null; rebuild = delegate {
                rows.Children.Clear(); accountCount.Text = text.F("accountCount", settings.Accounts.Count); int index = 0;
                foreach (var profile in settings.Accounts.ToArray()) {
                    index++; var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new Border { Width = 25, Height = 25, CornerRadius = new CornerRadius(8), Background = Color("#EAF1FF"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = index.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11, Foreground = Color("#5E88CE"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
                    var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(labels, 1); labels.Children.Add(new TextBlock { Text = profile.Name, FontSize = 12, Foreground = Color("#354155"), TextTrimming = TextTrimming.CharacterEllipsis }); labels.Children.Add(Label(text[profile.Id == settings.LastId ? "recentShort" : "independentLogin"], 10, "#9BA5B5")); row.Children.Add(labels);
                    var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(tools, 2);
                    var rename = UiTheme.Button(shell.View, "✎", "IconButton"); rename.Name = "RenameAccount"; rename.ToolTip = text["rename"]; rename.Click += async delegate { await Rename(profile); rebuild(); }; tools.Children.Add(rename);
                    var remove = UiTheme.Button(shell.View, "", "DangerQuietButton"); remove.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,1 L 11,1"), Stroke = Color("#CC5A58"), StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 12, Height = 3, Stretch = Stretch.Fill }; remove.Background = Color("#FFF0EE"); System.Windows.Automation.AutomationProperties.SetName(remove, text["remove"]); remove.Name = "RemoveAccount"; remove.MinWidth = 0; remove.Width = 28; remove.Height = 28; remove.Padding = new Thickness(0); remove.ToolTip = text["remove"]; remove.Click += async delegate { await Remove(profile); rebuild(); }; tools.Children.Add(remove); row.Children.Add(tools);
                    var card = UiTheme.Card(row); card.Padding = new Thickness(10, 9, 8, 9); card.Margin = new Thickness(0, 0, 0, 6); rows.Children.Add(card);
                }
                if (settings.Accounts.Count == 0) rows.Children.Add(Label(text["noAccounts"], 11, "#8C98AA"));
            }; rebuild(); pane.Children.Add(new ScrollViewer { Style = (Style)shell.View.FindResource("AppScrollViewer"), Content = rows, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            Section(pane, "preferences", null); pane.Children.Add(Label(text["language"], 12, "#3B485C"));
            var language = new ComboBox { Name = "LanguagePicker", ItemsSource = UiText.Languages, SelectedItem = UiText.Languages.First(x => x.Id == text.Language), Margin = new Thickness(0, 6, 0, 5) }; pane.Children.Add(language); pane.Children.Add(Label(text["languageHint"], 11, "#8A96A9"));
            language.DropDownClosed += delegate { Window.Dispatcher.BeginInvoke(new Action(() => { if (!demo && Window.IsVisible && !Window.IsActive && !pickerOpen) Hide(); })); };
            Section(pane, "desktopApp", null); bool automatic = String.IsNullOrWhiteSpace(settings.AppExePath), fillingPath = false, loadedPath = false;
            var pathMode = Label(text[automatic ? "autoMode" : "manualMode"], 10, "#7E8DA4"); pathMode.Name = "AppPathMode"; pathMode.Margin = new Thickness(0, 0, 0, 6); pane.Children.Add(pathMode);
            var path = new TextBox { Name = "AppPath", Text = settings.AppExePath ?? "", FontSize = 11, MaxLength = 2048, TextWrapping = TextWrapping.Wrap, MinHeight = 36, MaxHeight = 82, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 7) }; pane.Children.Add(path);
            var pathTools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) }; pane.Children.Add(pathTools);
            var browse = Button(shell.View, "browse", "QuietButton"); var detect = Button(shell.View, "autoDetect", "QuietButton"); var check = Button(shell.View, "checkApp", "QuietButton");
            browse.Name = "BrowseApp"; detect.Name = "DetectApp"; check.Name = "CheckApp";
            foreach (var tool in new[] { browse, detect, check }) { tool.MinWidth = 0; tool.FontSize = 11; tool.Padding = new Thickness(7, 6, 7, 6); pathTools.Children.Add(tool); }
            var pathStatus = Label(text["pathHint"], 11, "#8A96A9"); pathStatus.Name = "AppPathStatus"; pane.Children.Add(pathStatus);
            var pathHelp = Label(text["pathHint"], 11, "#8A96A9"); pathHelp.Margin = new Thickness(0, 4, 0, 0); pane.Children.Add(pathHelp);
            Action<string, bool> fillPath = (value, auto) => { fillingPath = true; try { path.Text = value; automatic = auto; path.ToolTip = value; pathMode.Text = text[auto ? "autoMode" : "manualMode"]; } finally { fillingPath = false; } };
            path.TextChanged += delegate { if (fillingPath) return; automatic = String.IsNullOrWhiteSpace(path.Text); pathMode.Text = text[automatic ? "autoMode" : "manualMode"]; path.ToolTip = path.Text; pathStatus.Text = text["pathEdited"]; };
            Func<bool, Task> verify = async auto => {
                if (shell.IsBusy || shell.Completion.IsCompleted) return;
                string candidate = auto ? "" : path.Text.Trim(); if (candidate.Length == 0) auto = true;
                shell.IsBusy = true; shell.Body.IsEnabled = shell.Footer.IsEnabled = false; SetBusy(true); pathStatus.Text = text["checking"];
                try {
                    var result = demo ? resolveSettings(candidate) : await Task.Run(() => resolveSettings(candidate));
                    fillPath(result.Exe, false); pathStatus.Text = text[System.IO.Path.GetExtension(candidate).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "shortcutReady" : "clientReady"];
                } catch { pathStatus.Text = text["clientInvalid"]; }
                finally { shell.IsBusy = false; shell.Body.IsEnabled = shell.Footer.IsEnabled = true; SetBusy(false); }
            };
            browse.Click += async delegate {
                if (demo || shell.IsBusy) return; pickerOpen = true;
                try { using (var picker = new Forms.OpenFileDialog { Title = text["chooseClient"], Filter = "ChatGPT.exe / shortcuts|ChatGPT.exe;*.lnk|ChatGPT.exe|ChatGPT.exe|Shortcuts (*.lnk)|*.lnk", DereferenceLinks = false, CheckFileExists = true, Multiselect = false }) {
                    if (picker.ShowDialog(new PickerOwner(new WindowInteropHelper(Window).Handle)) == Forms.DialogResult.OK) fillPath(picker.FileName, false); else return;
                } } catch { pathStatus.Text = text["clientInvalid"]; return; } finally { pickerOpen = false; Window.Activate(); }
                await verify(false);
            };
            detect.Click += async delegate { await verify(true); };
            check.Click += async delegate { await verify(automatic); };
            shell.View.Loaded += async delegate { if (!loadedPath) { loadedPath = true; if (automatic) await verify(true); else pathStatus.Text = text["pathSaved"]; } };
            Section(pane, "autoRefresh", null); pane.Children.Add(Label(text["refreshInterval"], 12, "#3B485C"));
            var quotaOptions = QuotaRefreshPolicy.Minutes.Select(minutes => new LanguageChoice(minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), minutes == 0 ? text["refreshNever"] : Duration(minutes))).ToArray();
            var quotaInterval = new ComboBox { Name = "QuotaInterval", ItemsSource = quotaOptions, SelectedItem = quotaOptions.First(x => x.Id == settings.QuotaRefreshMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)), Margin = new Thickness(0, 6, 0, 5) }; pane.Children.Add(quotaInterval); pane.Children.Add(Label(text["refreshHint"], 11, "#8A96A9"));
            quotaInterval.DropDownClosed += delegate { Window.Dispatcher.BeginInvoke(new Action(() => { if (!demo && Window.IsVisible && !Window.IsActive && !pickerOpen) Hide(); })); };
            Section(pane, "projectsHistory", text["afterReopen"]);
            Func<string, string, bool, CheckBox> option = (title, hint, enabled) => {
                var labels = new StackPanel(); labels.Children.Add(Label(text[title], 12, "#3B485C")); var note = Label(text[hint], 11, "#8A96A9"); note.Margin = new Thickness(0, 4, 0, 0); labels.Children.Add(note);
                var checkbox = new CheckBox { Content = labels, IsChecked = enabled }; var card = UiTheme.Card(checkbox); card.Margin = new Thickness(0, 0, 0, 7); pane.Children.Add(card); return checkbox;
            };
            var projects = option("syncProjects", "syncProjectsHint", settings.ShareProjects); var shared = option("shareHistory", "shareHistoryHint", settings.ShareLocalState);
            shared.Name = "SharedHistoryMode"; shared.IsEnabled = false; pane.Children.Add(Label(text["historyModeHint"], 11, "#929DAF"));
            pane.Children.Add(Label(text["sharingHint"], 11, "#929DAF"));
            bool startupWasEnabled = false; if (!demo) try { startupWasEnabled = StartupRegistration.IsEnabled(); } catch { }
            Section(pane, "startup", null); var startup = option("startup", "startupHint", startupWasEnabled); startup.Name = "StartWithWindows";
            Section(pane, "about", null); pane.Children.Add(Label(text.F("authorLabel", ProjectLinks.Author), 12, "#3B485C"));
            var support = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; pane.Children.Add(support);
            foreach (var kind in new[] { ProjectLink.Repository, ProjectLink.Feedback }) {
                var target = kind; string label = text[kind == ProjectLink.Repository ? "projectLink" : "feedbackLink"];
                var link = UiTheme.Button(shell.View, label + " ↗", "QuietButton"); link.Name = "Support" + kind; link.MinWidth = 0; link.FontSize = 11; link.Padding = new Thickness(7, 7, 7, 7); link.ToolTip = ProjectLinks.Address(kind);
                link.Click += delegate { if (!demo) try { ProjectLinks.Open(target); } catch { shell.ShowError(text["linkFailed"]); } }; support.Children.Add(link);
            }
            var cancel = Button(shell.View, "cancel", "QuietButton"); cancel.Margin = new Thickness(0, 0, 8, 0); cancel.Click += delegate { shell.Complete(false); }; shell.Footer.Children.Add(cancel);
            var done = Button(shell.View, "save", "PrimaryButton"); done.IsDefault = true; shell.Footer.Children.Add(done);
            done.Click += async delegate {
                if (shell.IsBusy) return; string selectedPath = path.Text.Trim(); int selectedInterval = Int32.Parse(((LanguageChoice)quotaInterval.SelectedItem).Id, System.Globalization.CultureInfo.InvariantCulture); string selectedLanguage = ((LanguageChoice)language.SelectedItem).Id;
                int oldInterval = settings.QuotaRefreshMinutes; bool oldProjects = settings.ShareProjects, oldShared = settings.ShareLocalState; string oldDirectory = settings.SharedStateDirectory, oldPath = settings.AppExePath, oldLanguage = settings.Language;
                done.IsEnabled = cancel.IsEnabled = shell.Body.IsEnabled = false; shell.IsBusy = true; SetBusy(true); shell.ShowError(null);
                string earlierStartup = null, expectedStartup = null, failureKey = "settingsWriteFailed"; bool startupApplied = false;
                try {
                    OfficialInstall official = null;
                    if (ClientResolutionPolicy.NeedsValidation(selectedPath, oldPath)) { failureKey = "clientSelectionFailed"; done.Content = text["checking"]; official = demo ? resolveSettings(selectedPath) : await Task.Run(() => resolveSettings(selectedPath)); selectedPath = official.Exe; }
                    if (!demo && shared.IsChecked == true && !oldShared) { done.Content = text["checking"]; if (official == null) official = await Task.Run(() => resolveSettings(selectedPath)); settings.SharedStateDirectory = await Task.Run(() => LocalWorkState.ResolveDefault(official, store)); }
                    settings.ShareProjects = projects.IsChecked == true; settings.ShareLocalState = shared.IsChecked == true; settings.AppExePath = selectedPath.Length == 0 ? null : selectedPath; settings.Language = selectedLanguage; settings.QuotaRefreshMinutes = selectedInterval;
                    if (!demo) {
                        failureKey = "startupSaveFailed";
                        if ((startup.IsChecked == true) != startupWasEnabled) { earlierStartup = StartupRegistration.Capture(); StartupRegistration.SetEnabled(startup.IsChecked == true); expectedStartup = startup.IsChecked == true ? StartupRegistration.CurrentCommand : null; startupApplied = true; }
                        failureKey = "settingsWriteFailed";
                        if (store != null) store.Save(settings);
                    }
                    SetLanguage(selectedLanguage); ConfigureQuotaTimer(); shell.IsBusy = false; shell.Complete(true);
                } catch { if (startupApplied) try { StartupRegistration.Rollback(expectedStartup, earlierStartup); } catch { } settings.ShareProjects = oldProjects; settings.ShareLocalState = oldShared; settings.SharedStateDirectory = oldDirectory; settings.AppExePath = oldPath; settings.Language = oldLanguage; settings.QuotaRefreshMinutes = oldInterval; shell.ShowError(new UiText(selectedLanguage)[failureKey]); }
                finally { shell.IsBusy = false; SetBusy(false); done.IsEnabled = cancel.IsEnabled = shell.Body.IsEnabled = true; done.Content = text["save"]; }
            }; return shell;
        }
        async Task Manage() { if (demo || busy || ActivePage != null) return; if (HistoryPending()) { Say(text["historyRecoveryNeeded"]); return; } SetBusy(true); try { await StopRefresh(); } finally { SetBusy(false); } try { await ShowPageAsync(CreateManagementDialog()); Render(); } catch { Say(text["settingsFailed"]); } }
        public DialogShell CreateHistoryDialog(out CheckBox enable) {
            var shell = Page(text["organizeHistory"], text["historySubtitle"]);
            shell.Body.Children.Add(Label(text["historyCloseWarning"], 12, "#A16B49"));
            var shared = new CheckBox { Name = "OrganizeSharedHistory", IsChecked = settings.ShareLocalState || settings.LocalHistoryLayout == 0, Content = Label(text["historyEnable"], 12, "#3B485C"), Margin = new Thickness(0, 14, 0, 10) }; shell.Body.Children.Add(shared); enable = shared;
            shell.Body.Children.Add(Label(text["historyStorageHint"], 11, "#8A96A9")); shell.Body.Children.Add(Label(text["historyParallelHint"], 11, "#8A96A9"));
            var cancel = Button(shell.View, "cancel", "QuietButton"); cancel.Margin = new Thickness(0, 0, 8, 0); cancel.Click += delegate { shell.Complete(false); }; shell.Footer.Children.Add(cancel);
            var run = Button(shell.View, "historyRun", "PrimaryButton"); run.Name = "RunHistoryOrganization"; run.Click += delegate { shell.Complete(true); }; shell.Footer.Children.Add(run); return shell;
        }
        async Task OrganizeHistory() {
            if (demo || busy || ActivePage != null) return;
            if (!NeedStore()) return;
            if (storageProblem != null) { await ShowPageAsync(CreateStorageDialog()); return; }
            SetBusy(true); try { await StopRefresh(); } finally { SetBusy(false); }
            CheckBox choice; var confirm = CreateHistoryDialog(out choice); if (!await ShowPageAsync(confirm)) return;
            bool enabled = choice.IsChecked == true; SetBusy(true); Say(text["historyClosing"]);
            try {
                var official = await Client();
                var result = await Task.Run(() => {
                    using (var held = official.HoldVerifiedFiles()) {
                        if (!ClientLifetime.StopAll(official, true, ignored => { }) || ClientLifetime.Inventory(official).Count != 0) throw new IOException();
                        Func<bool> closed = () => ClientLifetime.Inventory(official).Count == 0;
                        CompleteHistorySharing.Recover(store, settings, closed);
                        if (!enabled) { CompleteHistorySharing.Detach(store, settings, store.HomeFor(new Profile { Id = "Default" }), closed); return (HistoryOrganizationResult)null; }
                        string canonical = store.HomeFor(new Profile { Id = "Default" }), state = LocalWorkState.ResolveDefault(official, store);
                        using (var indexer = new HistoryIndexSession(official)) {
                            Func<bool> noOtherClient = () => ClientLifetime.Inventory(official).All(p => p.Id == indexer.ProcessId);
                            return CompleteHistorySharing.Organize(store, settings, canonical, state, noOtherClient, key => Window.Dispatcher.BeginInvoke(new Action(() => Say(text[key]))), indexer.Check, indexer.Dispose);
                        }
                    }
                });
                bool projectFailed = false;
                if (enabled) try { await Task.Run(() => {
                    using (var held = official.HoldVerifiedFiles()) foreach (var profile in settings.Accounts) {
                        if (ClientLifetime.Inventory(official).Count != 0) throw new IOException();
                        ProjectSync.Run(official, store, settings, profile); var plan = ProjectSidebarSync.Prepare(official, store, settings, profile);
                        if (plan != null) ProjectSidebarSync.Apply(store, profile, plan, () => ClientLifetime.Inventory(official).Count == 0);
                    }
                }); } catch { projectFailed = true; }
                Render(); Say(enabled ? text.F("historyComplete", result.Accounts, result.VerifiedThreads) + (projectFailed ? " " + text["syncFailed"] : "") : text["historyPrivateComplete"]);
            } catch (HistoryConflictException) { Say(text["historyConflict"]); }
            catch (HistorySetupRequiredException) { Say(text["historyRecoveryNeeded"]); }
            catch { Say(text["historyFailed"]); }
            finally { SetBusy(false); }
        }
        bool CanQuery(Profile profile) { DateTime time; return !queried.TryGetValue(profile.Id, out time) || DateTime.UtcNow - time >= TimeSpan.FromSeconds(30); }
        async Task<OfficialInstall> Client() {
            var official = await Task.Run(() => OfficialInstall.Find(settings));
            if (!ClientResolutionPolicy.Remember(settings, official, state => { if (store != null) store.Save(state); })) Say(text["pathSessionOnly"]);
            return official;
        }
        Task RefreshAll(bool automatic = false) {
            if (!demo && store == null) return Task.FromResult(0);
            if (!demo && HistoryPending()) { Say(text["historyRecoveryNeeded"]); return Task.FromResult(0); }
            if (demo && !syntheticRefresh || exiting || settings.Accounts.Count == 0 || !QuotaRefreshPolicy.MayStart(busy, refreshing, ActivePage != null)) return Task.FromResult(0);
            refresh.IsEnabled = false; refresh.Content = "↻  " + text["working"];
            return quotaRunner.Start();
        }
        async Task RefreshCore(CancellationToken cancellation) {
            int failed = 0; long progress = -1;
            try {
                cancellation.ThrowIfCancellationRequested(); var official = await Client(); int index = 0;
                using (var held = await Task.Run(() => official.HoldVerifiedFiles())) foreach (var profile in settings.Accounts.ToArray()) {
                    cancellation.ThrowIfCancellationRequested(); index++; Say(text.F("refreshing", index, settings.Accounts.Count)); progress = statusVersion;
                    if (!CanQuery(profile)) continue;
                    try { await Task.Run(() => AccountQuery.Read(official, store, profile, cancellation, settings.LocalHistoryLayout == 1 ? LocalWorkState.DirectoryForProfile(store, settings, profile) : null)); queried[profile.Id] = DateTime.UtcNow; }
                    catch (OperationCanceledException) { throw; } catch { cancellation.ThrowIfCancellationRequested(); failed++; }
                }
                Render(); Say(failed > 0 ? text["refreshIncomplete"] : null);
            } catch (OperationCanceledException) { if (statusVersion == progress) Say(null); }
            catch { if (!cancellation.IsCancellationRequested) Say(text["appMissing"]); }
            finally { refresh.IsEnabled = !busy; refresh.Content = "↻  " + text[busy ? "working" : "refresh"]; }
        }
        async Task OpenCurrent() { if (demo || busy) return; var profile = settings.Accounts.FirstOrDefault(x => x.Id == settings.LastId) ?? settings.Accounts.FirstOrDefault(); if (profile == null) { await Add(); return; } await WakeAccount(profile); }
        async Task WakeAccount(Profile profile) {
            if (!demo && !NeedStore()) return;
            if (!demo && HistoryPending()) { Say(text["historyRecoveryNeeded"]); return; }
            if (demo || busy) return; SetBusy(true); Say(text["checking"]); await StopRefresh(); bool switchNeeded = false; OfficialInstall official = null;
            try {
                official = await Client(); var result = await Task.Run(() => official.WakeExisting(store, profile, settings));
                if (result != null) {
                    if (!result.WindowFound) { Say(text.F("restoring", profile.Name)); using (var held = await Task.Run(() => official.HoldVerifiedFiles())) { if (!result.StateUnverified) await Task.Run(() => LocalWorkState.ValidateProfile(official, store, settings, profile)); result = await Task.Run(() => official.Open(store, profile, settings)); } }
                    settings.LastId = profile.Id; try { store.Save(settings); } catch { } Render(); Say(result.WindowFound ? text.F("restored", profile.Name) + (result.StateUnverified ? " " + text["externalMode"] : "") : text["windowPending"]);
                } else switchNeeded = true;
            } catch (HistorySetupRequiredException) { Say(text["historySetupNeeded"]); } catch (AccountDataModeChangedException) { Say(text["modeChanged"]); } catch (StateDirectoryConflictException) { Say(text["stateConflict"]); } catch { Say(text["wakeFailed"]); } finally { SetBusy(false); }
            if (switchNeeded) await Switch(profile, official);
        }
        public DialogShell CreateLaunchDialog(Profile profile, ClientProcessSummary summary, Action<LaunchMode> select) {
            var shell = Page(text.F("openAccount", profile.Name), summary.Describe(text)); var hint = Label(text["chooseMode"], 12, "#8B97A9"); hint.Margin = new Thickness(0, 0, 0, 10); shell.Body.Children.Add(hint);
            Action<string, string, string, LaunchMode, string> add = (title, detail, glyph, mode, style) => {
                var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.Children.Add(Label(glyph, 20, mode == LaunchMode.Parallel ? "#3977EF" : "#B88360"));
                var labels = new StackPanel(); Grid.SetColumn(labels, 1); var heading = Label(text[title], 13, mode == LaunchMode.Parallel ? "#3E6CB6" : "#886649"); heading.FontWeight = FontWeights.SemiBold; labels.Children.Add(heading); var sub = Label(text[detail], 11, "#8995A7"); sub.Margin = new Thickness(0, 5, 0, 0); labels.Children.Add(sub); row.Children.Add(labels);
                var button = UiTheme.Button(shell.View, "", style); button.Content = row; button.Margin = new Thickness(0, 0, 0, 9); button.Click += delegate { select(mode); shell.Complete(true); }; shell.Body.Children.Add(button);
            };
            add("parallel", "parallelHint", "↗", LaunchMode.Parallel, "PrimaryChoice"); add("closeAll", "closeAllHint", "⏻", LaunchMode.CloseExisting, "CarefulChoice"); shell.Body.Children.Add(Label(text["auxHint"], 11, "#929DAF"));
            var cancel = Button(shell.View, "notNow", "QuietButton"); cancel.Click += delegate { shell.Complete(false); }; shell.Footer.Children.Add(cancel); return shell;
        }
        async Task<LaunchMode> ChooseLaunch(Profile profile, OfficialInstall official, List<AppProcess> running) { var summary = await Task.Run(() => ClientLifetime.Summarize(official, running, ClientLifetime.ParentMap())); LaunchMode choice = LaunchMode.Cancel; var shell = CreateLaunchDialog(profile, summary, mode => choice = mode); return await ShowPageAsync(shell) ? choice : LaunchMode.Cancel; }
        async Task Switch(Profile profile, OfficialInstall inspected) {
            if (busy || demo) return; SetBusy(true); Say(text["preparing"]);
            try {
                var official = inspected ?? await Client(); var running = await Task.Run(() => ClientLifetime.Inventory(official)); var mode = LaunchPolicy.NeedsChoice(running.Count) ? await ChooseLaunch(profile, official, running) : LaunchMode.Open;
                if (mode == LaunchMode.Cancel) { Say(text["cancelled"]); return; }
                if (mode == LaunchMode.Parallel) {
                    var reused = await Task.Run(() => official.WakeExisting(store, profile, settings));
                    if (reused != null) { settings.LastId = profile.Id; try { store.Save(settings); } catch { } Render(); Say(reused.WindowFound ? text.F("restored", profile.Name) + (reused.StateUnverified ? " " + text["externalMode"] : "") : text["windowPending"]); return; }
                }
                using (var held = await Task.Run(() => official.HoldVerifiedFiles())) {
                await Task.Run(() => CompleteHistorySharing.PrepareNewProfile(store, settings, profile, () => !official.IsKnownWindowRunning(store, profile) && official.InspectExisting(store, profile) == null));
                await Task.Run(() => LocalWorkState.ValidateProfile(official, store, settings, profile)); bool syncFailed = false, syncDeferred = false;
                if (settings.ShareProjects) try { Say(text["prepareProjects"]); await Task.Run(() => ProjectSync.Run(official, store, settings, profile)); } catch { syncFailed = true; }
                SidebarPlan sidebar = null;
                if (settings.ShareProjects && settings.ShareLocalState) try { sidebar = await Task.Run(() => ProjectSidebarSync.Prepare(official, store, settings, profile)); } catch { syncFailed = true; }
                running = await Task.Run(() => ClientLifetime.Inventory(official));
                if (mode == LaunchMode.Open && running.Count > 0) { mode = await ChooseLaunch(profile, official, running); if (mode == LaunchMode.Cancel) { Say(text["cancelled"]); return; } }
                ClientOpenResult opened = null;
                bool completed = await Task.Run(() => {
                    Action launch = () => {
                        if (sidebar != null) try { syncDeferred = ProjectSidebarSync.Apply(store, profile, sidebar, () => ClientLifetime.CanEditSidebar(official, store, profile)).Deferred; } catch { syncFailed = true; }
                        opened = official.Open(store, profile, settings);
                    };
                    if (LaunchPolicy.MustClose(mode)) return SwitchSequence.CloseThenOpen(() => ClientLifetime.StopAll(official, true, ignored => Window.Dispatcher.BeginInvoke(new Action(() => Say(text["closing"])))) && LaunchPolicy.MayOpen(mode, ClientLifetime.Inventory(official).Count), launch);
                    if (!LaunchPolicy.MayOpen(mode, ClientLifetime.Inventory(official).Count)) return false; launch(); return true;
                });
                if (!completed) { Say(text["closeFailed"]); return; }
                settings.LastId = profile.Id; try { store.Save(settings); } catch { } Render();
                Say((opened.WindowFound ? text.F("opened", profile.Name) : text.F("opening", profile.Name)) + (syncFailed ? " " + text["syncFailed"] : syncDeferred ? " " + text["syncDeferred"] : ""));
                if (CanQuery(profile)) try { await Task.Run(() => AccountQuery.Read(official, store, profile, CancellationToken.None, settings.LocalHistoryLayout == 1 ? LocalWorkState.DirectoryForProfile(store, settings, profile) : null)); queried[profile.Id] = DateTime.UtcNow; Render(); } catch { Say(text.F("opening", profile.Name) + " " + text["usageUnavailable"]); }
                }
            } catch (HistorySetupRequiredException) { Say(text["historySetupNeeded"]); } catch (AccountDataModeChangedException) { Say(text["modeChanged"]); } catch (StateDirectoryConflictException) { Say(text["stateConflict"]); } catch { Say(text["openFailed"]); } finally { SetBusy(false); }
        }
    }
}
