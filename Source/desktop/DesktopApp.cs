using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("GPT Switcher")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
[assembly: AssemblyDescription("Local Windows tray panel for independently signed-in ChatGPT accounts")]
[assembly: AssemblyProduct("GPT Switcher")]
[assembly: AssemblyCompany("git-buster")]
[assembly: AssemblyCopyright("Copyright git-buster")]
[assembly: AssemblyVersion("0.6.14.0")]
[assembly: AssemblyFileVersion("0.6.14.0")]

namespace DesktopAccounts {
    public sealed class Profile {
        public string Id { get; set; }
        public string Name { get; set; }
    }
    public sealed class Settings {
        public int Version { get; set; }
        public bool ShareProjects { get; set; }
        public bool ShareLocalState { get; set; }
        public int LocalHistoryLayout { get; set; }
        public string SharedStateDirectory { get; set; }
        public string LastId { get; set; }
        public string Language { get; set; }
        public string AppExePath { get; set; }
        public int QuotaRefreshMinutes { get; set; }
        public List<Profile> Accounts { get; set; }
        public Settings() { Version = 2; Language = "en"; Accounts = new List<Profile>(); }
    }
    public sealed class QuotaWindow {
        public double? RemainingPercent { get; set; }
        public int? WindowDurationMins { get; set; }
        public long? ResetsAt { get; set; }
    }
    public sealed class Bucket {
        public string LimitId { get; set; }
        public string LimitName { get; set; }
        public QuotaWindow Primary { get; set; }
        public QuotaWindow Secondary { get; set; }
    }
    public sealed class Snapshot {
        public int SchemaVersion { get; set; }
        public string Profile { get; set; }
        public string Email { get; set; }
        public string PlanType { get; set; }
        public string UpdatedAtUtc { get; set; }
        public List<Bucket> Buckets { get; set; }
    }
    public static class Safe {
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 1048576, RecursionLimit = 30 }; }
        public static string Wire(object value) {
            var text = Json().Serialize(value); var ascii = new StringBuilder(text.Length);
            foreach (char c in text) { if (c > 127) ascii.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else ascii.Append(c); }
            return ascii.ToString();
        }
        public static bool Id(string id) { Guid value; return id == "Default" || id == "A" || (id != null && id.Length == 32 && Guid.TryParseExact(id, "N", out value)); }
        public static void Validate(Settings s) {
            if (s == null || s.Version != 2 && s.Version != 3 || s.Accounts == null || s.Accounts.Count > 500) throw new InvalidDataException();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in s.Accounts)
                if (p == null || !Id(p.Id) || !ids.Add(p.Id) || String.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 40 || p.Name.Any(Char.IsControl)) throw new InvalidDataException();
            if (s.LastId != null && !ids.Contains(s.LastId)) s.LastId = null;
            s.Language = UiText.Normalize(s.Language);
            s.QuotaRefreshMinutes = QuotaRefreshPolicy.Normalize(s.QuotaRefreshMinutes);
            if (s.LocalHistoryLayout < 0 || s.LocalHistoryLayout > 1) throw new InvalidDataException();
            if (s.LocalHistoryLayout == 1) s.Version = 3;
            if (s.AppExePath != null && (s.AppExePath.Length > 2048 || s.AppExePath.Any(Char.IsControl))) throw new InvalidDataException();
            if (s.ShareLocalState && (String.IsNullOrWhiteSpace(s.SharedStateDirectory) || !Path.IsPathRooted(s.SharedStateDirectory))) throw new InvalidDataException();
        }
        public static void PlainPath(string path) {
            for (var d = new DirectoryInfo(Path.GetFullPath(path)); d != null; d = d.Parent)
                if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException();
        }
        public static string Mask(string email) {
            if (String.IsNullOrEmpty(email)) return "尚未读取账号信息";
            int at = email.IndexOf('@'); return at > 0 ? email.Substring(0, 1) + "•••" + email.Substring(at) : "账号信息已隐藏";
        }
        public static DateTime? Date(string s) { DateTime d; return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d) ? (DateTime?)d.ToLocalTime() : null; }
        public static string Stamp(string s) { var d = Date(s); return d.HasValue ? d.Value.ToString("MM/dd HH:mm") : "未知"; }
        public static Dictionary<string, object> Map(object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static object Field(object o, string key) { object value; return Map(o).TryGetValue(key, out value) ? value : null; }
        public static string Text(object o, int max) { var s = o as string; return s == null || s.Length > max || s.Any(Char.IsControl) ? null : s; }
        public static void CleanEnvironment(ProcessStartInfo start, string home) { CleanEnvironment(start, home, null); }
        public static void CleanEnvironment(ProcessStartInfo start, string home, string sqliteHome) {
            foreach (string k in start.EnvironmentVariables.Keys.Cast<string>().ToArray())
                if (Regex.IsMatch(k, "^(CODEX_|OPENAI_|CHATGPT_|RUST_LOG$|RUST_BACKTRACE$|NODE_OPTIONS$|NODE_PATH$|ELECTRON_RUN_AS_NODE$)", RegexOptions.IgnoreCase)) start.EnvironmentVariables.Remove(k);
            start.EnvironmentVariables.Remove("PSModulePath");
            if (home != null) start.EnvironmentVariables["CODEX_HOME"] = home;
            if (sqliteHome != null) start.EnvironmentVariables["CODEX_SQLITE_HOME"] = sqliteHome;
        }
    }
    public sealed class PrivateStore {
        public readonly string Root;
        const string Marker = "ChatGPT desktop account panel v2";
        public PrivateStore(string root) {
            Root = Path.GetFullPath(root); Safe.PlainPath(Root);
            if (!Directory.Exists(Root)) {
                Directory.CreateDirectory(Root);
                var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WindowsIdentity.GetCurrent().User, new SecurityIdentifier("S-1-5-18") })
                    acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(Root, acl);
                File.WriteAllText(Path.Combine(Root, ".switcher-root"), Marker, new UTF8Encoding(false));
            }
            var rules = Directory.GetAccessControl(Root); string own = WindowsIdentity.GetCurrent().User.Value;
            if (!rules.AreAccessRulesProtected || rules.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r => r.IdentityReference.Value != own && r.IdentityReference.Value != "S-1-5-18")) throw new IOException();
            string marker = Path.Combine(Root, ".switcher-root"), legacy = Path.Combine(Root, ".probe-root");
            Safe.PlainPath(marker); Safe.PlainPath(legacy);
            if (File.Exists(marker)) { if (File.ReadAllText(marker) != Marker) throw new IOException(); }
            else if (File.Exists(legacy) && File.ReadAllText(legacy) == "Native Work isolation probe v1") File.WriteAllText(marker, Marker, new UTF8Encoding(false));
            else throw new IOException();
        }
        public Settings Load() {
            string file = Path.Combine(Root, "accounts.dat");
            if (File.Exists(file)) { var s = Decode<Settings>(file); Safe.Validate(s); return s; }
            var fresh = new Settings(); fresh.Accounts.Add(new Profile { Id = "Default", Name = new UiText("en")["defaultAccount"] });
            if (Directory.Exists(Path.Combine(Root, "A", "desktop")) && Directory.Exists(Path.Combine(Root, "A", "codex"))) fresh.Accounts.Add(new Profile { Id = "A", Name = new UiText("en").F("accountName", 2) });
            Save(fresh); return fresh;
        }
        public void Save(Settings s) { Safe.Validate(s); Encode(Path.Combine(Root, "accounts.dat"), s); }
        public string DirectoryFor(Profile p) {
            if (p == null || !Safe.Id(p.Id) || p.Id == "Default") throw new InvalidDataException();
            string path = p.Id == "A" ? Path.Combine(Root, "A") : Path.Combine(Root, "Profiles", p.Id); Safe.PlainPath(path); return path;
        }
        public string DesktopFor(Profile p) { return Path.Combine(DirectoryFor(p), "desktop"); }
        public string HomeFor(Profile p) { return p.Id == "Default" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : Path.Combine(DirectoryFor(p), "codex"); }
        public void Prepare(Profile p) {
            if (p.Id == "Default") return;
            foreach (string d in new[] { DesktopFor(p), HomeFor(p) }) { Safe.PlainPath(d); Directory.CreateDirectory(d); }
        }
        string SnapshotFile(Profile p) { return p.Id == "Default" ? Path.Combine(Root, "default-status.dat") : Path.Combine(DirectoryFor(p), "status.dat"); }
        public Snapshot Read(Profile p) {
            try {
                var s = Decode<Snapshot>(SnapshotFile(p));
                if (s == null || s.Profile != p.Id || s.Buckets == null || s.Buckets.Count > 30 || Safe.Text(s.Email, 254) == null || Safe.Text(s.PlanType, 60) == null) return null;
                return s;
            } catch { return null; }
        }
        public void SaveSnapshot(Profile p, Snapshot s) { Prepare(p); Encode(SnapshotFile(p), s); }
        string CheckFile(string file) {
            string resolved = Path.GetFullPath(file);
            if (!resolved.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Metadata must stay inside the switcher's private directory.");
            Safe.PlainPath(resolved);
            return resolved;
        }
        public T Decode<T>(string file) { return DecodeSized<T>(file, 1048576); }
        public T DecodeHistoryJournal<T>(string file) { return DecodeSized<T>(file, 67108864); }
        T DecodeSized<T>(string file, int limit) {
            file = CheckFile(file); if (new FileInfo(file).Length > limit) throw new IOException();
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
            try { var serializer = Safe.Json(); serializer.MaxJsonLength = limit; return serializer.Deserialize<T>(Encoding.UTF8.GetString(plain)); } finally { Array.Clear(plain, 0, plain.Length); }
        }
        public void Encode(string file, object data) { EncodeSized(file, data, 1048576); }
        public void EncodeHistoryJournal(string file, object data) { EncodeSized(file, data, 67108864); }
        void EncodeSized(string file, object data, int limit) {
            file = CheckFile(file); var serializer = Safe.Json(); serializer.MaxJsonLength = limit; byte[] plain = Encoding.UTF8.GetBytes(serializer.Serialize(data)), encrypted;
            try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); } finally { Array.Clear(plain, 0, plain.Length); }
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(encrypted, 0, encrypted.Length); output.Flush(true); }
            if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
        }
    }
    public sealed class StartupStorage {
        public PrivateStore Store;
        public Settings Settings;
        public string Problem;
        public static string Describe(Exception error) {
            while (error.InnerException != null) error = error.InnerException;
            if (error is UnauthorizedAccessException || error is System.Security.SecurityException) return "storagePermission";
            if (error is CryptographicException) return "storageEncryption";
            if (error is InvalidDataException) return "storageFormat";
            return "storageUnavailable";
        }
        public static Settings Fresh() { var value = new Settings(); value.Accounts.Add(new Profile { Id = "Default", Name = new UiText("en")["defaultAccount"] }); return value; }
        public static StartupStorage Open(string primary, string recovery) {
            try { var original = new PrivateStore(primary); return new StartupStorage { Store = original, Settings = original.Load() }; }
            catch (Exception error) {
                var result = new StartupStorage { Problem = Describe(error) };
                try {
                    if (String.Equals(Path.GetFullPath(primary), Path.GetFullPath(recovery), StringComparison.OrdinalIgnoreCase)) throw new IOException();
                    result.Store = new PrivateStore(recovery); result.Settings = result.Store.Load();
                } catch { result.Store = null; result.Settings = Fresh(); }
                return result;
            }
        }
    }
    public sealed class OfficialInstall {
        public string Exe, Cli, Family;
        sealed class Verified { public OfficialInstall Install; }
        string verifiedExe, verifiedCli, exeHash, cliHash;
        ClientBundleGuard bundleGuard;
        static readonly Dictionary<string, Verified> verified = new Dictionary<string, Verified>(StringComparer.OrdinalIgnoreCase);
        static readonly object verificationLock = new object();
        public static OfficialInstall Find() { return ResolveCandidate(null); }
        public static OfficialInstall Find(Settings settings) { return ClientResolutionPolicy.ResolveSaved(settings == null ? null : settings.AppExePath, ResolveCandidate, AutoDetect); }
        public static OfficialInstall AutoDetect() { lock (verificationLock) { verified.Remove(""); return ResolveCandidate(null); } }
        public static OfficialInstall ResolveCandidate(string candidate) {
            string key = String.IsNullOrWhiteSpace(candidate) ? "" : candidate.Trim();
            if (String.Equals(Path.GetExtension(key), ".lnk", StringComparison.OrdinalIgnoreCase)) {
                key = ClientShortcut.Read(key, target => { var app = Discover(); return ClientShortcut.MatchesStoreApp(target, app.Family) ? app.Exe : null; });
            }
            lock (verificationLock) {
                Verified cached;
                if (verified.TryGetValue(key, out cached) && File.Exists(cached.Install.Exe) && File.Exists(cached.Install.Cli)) {
                    using (var files = new ClientBundleFiles(cached.Install.Exe, cached.Install.Cli)) if (files.Matches(cached.Install.exeHash, cached.Install.cliHash)) return cached.Install;
                    verified.Remove(key);
                }
                OfficialInstall install;
                if (key.Length == 0) install = Discover();
                else {
                    ValidateCandidatePath(key); string exe = Path.GetFullPath(key);
                    install = new OfficialInstall { Exe = exe, Cli = Path.Combine(Path.GetDirectoryName(exe), "resources", "codex.exe"), Family = OfficialActivation.RegisteredFamily(exe) };
                }
                ValidateCandidatePath(install.Exe); Safe.PlainPath(install.Cli);
                if (!File.Exists(install.Exe) || !File.Exists(install.Cli)) throw new IOException("Supported official bundle not found.");
                using (var files = new ClientBundleFiles(install.Exe, install.Cli)) {
                var info = FileVersionInfo.GetVersionInfo(install.Exe);
                if (info.CompanyName == null || !info.CompanyName.StartsWith("OpenAI", StringComparison.Ordinal) || !new[] { "Codex", "ChatGPT", "ChatGPT Desktop" }.Contains(info.ProductName)) throw new IOException("Unsupported desktop client.");
                string[] paths = { install.Exe, install.Cli };
                string script = "$ErrorActionPreference='Stop';$allowed=@('OpenAI OpCo, LLC','OpenAI, LLC','OpenAI');foreach($f in @(" + String.Join(",", paths.Select(p => "'" + p.Replace("'", "''") + "'")) + ")){$s=Get-AuthenticodeSignature -LiteralPath $f;if($s.Status -ne 'Valid' -or $null -eq $s.SignerCertificate -or $allowed -notcontains $s.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false)){exit 3}};[Console]::Write('ok')";
                var start = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), Arguments = "-NoProfile -NonInteractive -Command \"" + script + "\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                Safe.CleanEnvironment(start, null);
                using (var process = Process.Start(start)) {
                    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(15000)) { process.Kill(); throw new IOException("Client verification timed out."); }
                    if (process.ExitCode != 0 || output.Result != "ok") throw new IOException("Client signature is not supported.");
                }
                install.verifiedExe = install.Exe; install.verifiedCli = install.Cli; install.exeHash = files.ExeHash; install.cliHash = files.CliHash;
                install.bundleGuard = new ClientBundleGuard(install.Exe, install.Cli, files.ExeHash, files.CliHash);
                var record = new Verified { Install = install }; verified[key] = record; verified[install.Exe] = record; return install;
                }
            }
        }
        public static void ValidateCandidatePath(string candidate) {
            ValidateLocalPath(candidate);
            if (!String.Equals(Path.GetFileName(candidate), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose the local official ChatGPT.exe.");
        }
        public static void ValidateLocalPath(string candidate) {
            if (String.IsNullOrEmpty(candidate) || candidate.Length > 2048 || candidate.Any(Char.IsControl) || candidate.Contains('"') || !Path.IsPathRooted(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Choose a local application.");
            string full = Path.GetFullPath(candidate), root = Path.GetPathRoot(full);
            if (Path.GetPathRoot(candidate).Length != 3 || new DriveInfo(root).DriveType == DriveType.Network) throw new IOException("Choose a local application.");
            Safe.PlainPath(full);
        }
        public Process StartVerified(ProcessStartInfo start) {
            if (verifiedExe == null || verifiedCli == null || Exe != verifiedExe || Cli != verifiedCli || !String.Equals(start.FileName, Exe, StringComparison.OrdinalIgnoreCase) && !String.Equals(start.FileName, Cli, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unverified client cannot be started.");
            using (HoldVerifiedFiles()) return Process.Start(start);
        }
        public IDisposable HoldVerifiedFiles() {
            if (bundleGuard == null || Exe != verifiedExe || Cli != verifiedCli) throw new IOException("Unverified client cannot be started.");
            return bundleGuard.Acquire();
        }
        static OfficialInstall Discover() {
            var start = new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);$p=@(Get-AppxPackage -Name 'OpenAI.Codex'); if($p.Count -eq 1){[pscustomobject]@{Location=$p[0].InstallLocation;Family=$p[0].PackageFamilyName}|ConvertTo-Json -Compress}else{exit 2}\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            Safe.CleanEnvironment(start, null);
            using (var p = Process.Start(start)) {
                var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15000)) { p.Kill(); throw new IOException(); }
                if (p.ExitCode != 0) throw new IOException();
                string dir = Safe.Text(Safe.Field(Safe.Json().DeserializeObject(output.Result), "Location"), 2048);
                if (String.IsNullOrEmpty(dir)) throw new IOException();
                var install = new OfficialInstall { Exe = Path.Combine(dir, "app", "ChatGPT.exe"), Cli = Path.Combine(dir, "app", "resources", "codex.exe"), Family = Safe.Text(Safe.Field(Safe.Json().DeserializeObject(output.Result), "Family"), 160) };
                if (!File.Exists(install.Exe) || !File.Exists(install.Cli)) throw new IOException(); return install;
            }
        }
        public ProcessStartInfo CreateStart(PrivateStore store, Profile profile, Settings settings) {
            store.Prepare(profile);
            var start = new ProcessStartInfo { FileName = Exe, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            if (profile.Id != "Default") start.Arguments = "--user-data-dir=\"" + store.DesktopFor(profile) + "\"";
            Safe.CleanEnvironment(start, profile.Id == "Default" ? null : store.HomeFor(profile), LocalWorkState.DirectoryForProfile(store, settings, profile));
            return start;
        }
        Process LaunchClient(PrivateStore store, Profile profile, Settings settings) {
            if (profile.Id != "Default") return StartVerified(CreateStart(store, profile, settings));
            string family = OfficialActivation.RegisteredFamily(Exe); Family = family;
            if (String.IsNullOrEmpty(family)) return StartVerified(CreateStart(store, profile, settings));
            if (verifiedExe == null || verifiedCli == null || Exe != verifiedExe || Cli != verifiedCli) throw new IOException("Unverified client cannot be activated.");
            using (HoldVerifiedFiles()) {
                return OfficialActivation.Start(OfficialActivation.ReadApplicationId(Exe, family));
            }
        }
        public ClientOpenResult WakeExisting(PrivateStore store, Profile profile, Settings settings) {
            var known = KnownWindows(store);
            var remembered = known.FirstOrDefault(p => p.ProfileId == profile.Id) ?? FindExternal(store, profile, known);
            if (remembered == null) return null;
            if (!remembered.StateUnverified && !LocalWorkState.CanReuse(profile.Id, remembered.StateHome, LocalWorkState.DirectoryForProfile(store, settings, profile))) throw new AccountDataModeChangedException();
            known.RemoveAll(p => p.ProfileId == profile.Id); known.Add(remembered);
            try { store.Encode(Path.Combine(store.Root, "windows.dat"), known); } catch { }
            // Old records may refer to a native shell previously forced visible. Reactivate
            // them through the official reopen contract once before trusting direct restoration.
            return new ClientOpenResult { WindowFound = remembered.LifecycleReady && ClientWindows.Wake(remembered), Reused = true, StateUnverified = remembered.StateUnverified };
        }
        public AppProcess InspectExisting(PrivateStore store, Profile profile) {
            var known = KnownWindows(store); return known.FirstOrDefault(p => p.ProfileId == profile.Id) ?? FindExternal(store, profile, known);
        }
        AppProcess FindExternal(PrivateStore store, Profile profile, List<AppProcess> known) {
            var all = ClientLifetime.Inventory(this).Where(p => String.Equals(p.Image, Exe, StringComparison.OrdinalIgnoreCase)).ToArray();
            var parents = ClientLifetime.ParentMap(); if (parents == null) return null;
            string directory = profile.Id == "Default" ? null : store.DesktopFor(profile);
            var matches = new List<AppProcess>();
            foreach (var candidate in all) {
                int parent;
                if (!parents.TryGetValue(candidate.Id, out parent) || all.Any(p => p.Id == parent) || known.Any(p => p.Id == candidate.Id && p.ProfileId != profile.Id)) continue;
                candidate.ProfileId = profile.Id;
                if (!IsLiveWindow(candidate)) continue;
                var arguments = ClientLaunchIdentity.Arguments(candidate.Id);
                if (arguments == null) throw new IOException("Existing client identity could not be checked; no new instance opened.");
                if (!ClientLaunchIdentity.MatchesArguments(arguments, directory) || !IsLiveWindow(candidate)) continue;
                // Unknown launch environments must never be reported as verified sharing modes.
                candidate.StateUnverified = profile.Id != "Default"; matches.Add(candidate);
            }
            var selected = ClientLaunchIdentity.Choose(matches, profile.Id == "Default", p => ClientLaunchIdentity.HasPackageIdentity(p.Id, Family));
            if (selected == null && matches.Count > 0) throw new IOException("Existing client identity is ambiguous; no new instance opened.");
            return selected;
        }
        public ClientOpenResult Open(PrivateStore store, Profile profile, Settings settings) {
            store.Prepare(profile);
            var known = KnownWindows(store);
            var remembered = known.FirstOrDefault(p => p.ProfileId == profile.Id) ?? FindExternal(store, profile, known);
            string stateHome = LocalWorkState.DirectoryForProfile(store, settings, profile);
            if (remembered != null && !remembered.StateUnverified && !LocalWorkState.CanReuse(profile.Id, remembered.StateHome, stateHome)) throw new AccountDataModeChangedException();
            if (remembered != null && remembered.LifecycleReady && ClientWindows.Wake(remembered)) return new ClientOpenResult { WindowFound = true, Reused = true, StateUnverified = remembered.StateUnverified };
            using (var process = LaunchClient(store, profile, settings)) {
                var timer = Stopwatch.StartNew(); AppProcess launched = null; long nextDiscovery = 0;
                // Wait for a real application window rather than reporting success at Process.Start.
                while (timer.ElapsedMilliseconds < 12000) {
                    if (remembered != null && IsLiveWindow(remembered) && ClientWindows.Wake(remembered)) {
                        SaveWindow(store, known, remembered);
                        return new ClientOpenResult { WindowFound = true, Reused = true, StateUnverified = remembered.StateUnverified };
                    }
                    if (process != null && !process.HasExited) {
                        if (launched == null) launched = new AppProcess { Id = process.Id, Started = process.StartTime.ToUniversalTime().ToFileTimeUtc(), ProfileId = profile.Id, Image = Exe, SessionId = process.SessionId, StateHome = stateHome };
                        if (ClientWindows.Wake(launched)) {
                            SaveWindow(store, known, launched);
                            return new ClientOpenResult { WindowFound = true };
                        }
                    }
                    // A single-instance launcher can exit while the original application is
                    // still creating its replacement window. Keep waiting for that exact profile.
                    if ((remembered == null || !IsLiveWindow(remembered)) && timer.ElapsedMilliseconds >= nextDiscovery) {
                        remembered = FindExternal(store, profile, known); nextDiscovery = timer.ElapsedMilliseconds + 600;
                    }
                    Thread.Sleep(150);
                }
                if (launched != null && IsLiveWindow(launched)) {
                    known.RemoveAll(p => p.ProfileId == profile.Id); known.Add(launched);
                    try { store.Encode(Path.Combine(store.Root, "windows.dat"), known); } catch { }
                }
                return new ClientOpenResult { Reused = remembered != null, StateUnverified = remembered != null && remembered.StateUnverified };
            }
        }
        static void SaveWindow(PrivateStore store, List<AppProcess> known, AppProcess window) {
            window.LifecycleReady = true; known.RemoveAll(p => p.ProfileId == window.ProfileId); known.Add(window);
            try { store.Encode(Path.Combine(store.Root, "windows.dat"), known); } catch { /* Bookkeeping never changes login data. */ }
        }
        public List<AppProcess> KnownWindows(PrivateStore store) {
            var known = new List<AppProcess>();
            try {
                string file = Path.Combine(store.Root, "windows.dat");
                if (File.Exists(file)) known = store.Decode<List<AppProcess>>(file) ?? known;
                else known.Add(store.Decode<AppProcess>(Path.Combine(store.Root, "last-window.dat")));
            } catch { return new List<AppProcess>(); }
            if (known.Count > 500) return new List<AppProcess>();
            return known.Where(IsLiveWindow).GroupBy(p => p.ProfileId).Select(g => g.First()).ToList();
        }
        bool IsLiveWindow(AppProcess known) {
            if (known == null || !Safe.Id(known.ProfileId) || !String.Equals(known.Image, Exe, StringComparison.OrdinalIgnoreCase)) return false;
            try { using (var live = Process.GetProcessById(known.Id)) {
                return !live.HasExited && live.SessionId == Process.GetCurrentProcess().SessionId && live.SessionId == known.SessionId && live.StartTime.ToUniversalTime().ToFileTimeUtc() == known.Started && String.Equals(live.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase);
            } } catch { return false; }
        }
        public bool IsKnownWindowRunning(PrivateStore store, Profile profile) {
            return KnownWindows(store).Any(p => p.ProfileId == profile.Id);
        }
    }
    public sealed class AppProcess { public int Id, SessionId; public long Started; public string ProfileId, Image, StateHome; public bool HasWindow, StateUnverified, LifecycleReady; }
    public sealed class ClientOpenResult { public bool WindowFound, Reused, StateUnverified; }
    public sealed class LocalRpcException : IOException {
        public readonly string Method; public readonly int? Code;
        public LocalRpcException(string method, object error) { Method = method; int code; Code = Int32.TryParse(Convert.ToString(Safe.Field(error, "code")), out code) ? (int?)code : null; }
    }
    public static class AccountQuery {
        internal static object Rpc(Process process, int id, string method, object args) { return Rpc(process, id, method, args, CancellationToken.None); }
        internal static object HistoryRpc(Process process, int id, string method, object args) {
            if (!new[] { "thread/read", "thread/resume", "thread/archive", "thread/turns/list" }.Contains(method)) throw new InvalidOperationException();
            return Rpc(process, id, method, args, CancellationToken.None, 120);
        }
        static object Rpc(Process process, int id, string method, object args, CancellationToken cancellation, int timeoutSeconds = 20) {
            cancellation.ThrowIfCancellationRequested();
            if (!new[] { "initialize", "account/read", "account/rateLimits/read", "project/list", "project/create", "config/read", "configRequirements/read", "thread/read", "thread/resume", "thread/archive", "thread/turns/list" }.Contains(method)) throw new InvalidOperationException();
            process.StandardInput.WriteLine(Safe.Wire(new { id = id, method = method, @params = args })); process.StandardInput.Flush();
            var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < until) {
                var read = process.StandardOutput.ReadLineAsync();
                if (!read.Wait(Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds), cancellation) || read.Result == null || read.Result.Length > 1048576) throw new IOException();
                object response; try { response = Safe.Json().DeserializeObject(read.Result); } catch { continue; }
                var value = Safe.Field(response, "id"); if (value == null || Convert.ToString(value, CultureInfo.InvariantCulture) != id.ToString(CultureInfo.InvariantCulture)) continue;
                if (Safe.Field(response, "error") != null) throw new LocalRpcException(method, Safe.Field(response, "error")); return Safe.Field(response, "result");
            }
            throw new IOException();
        }
        static QuotaWindow Window(object w) {
            if (w == null) return null;
            double used; int duration; long reset;
            var raw = Safe.Field(w, "usedPercent");
            double? remaining = raw != null && Double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out used) && !Double.IsNaN(used) && !Double.IsInfinity(used) ? (double?)Math.Max(0, Math.Min(100, 100 - used)) : null;
            return new QuotaWindow { RemainingPercent = remaining, WindowDurationMins = Int32.TryParse(Convert.ToString(Safe.Field(w, "windowDurationMins")), out duration) && duration > 0 && duration < 525600 ? (int?)duration : null, ResetsAt = Int64.TryParse(Convert.ToString(Safe.Field(w, "resetsAt")), out reset) && reset > 0 && reset < 253402300799 ? (long?)reset : null };
        }
        public static Snapshot Read(OfficialInstall official, PrivateStore store, Profile profile) { return Read(official, store, profile, CancellationToken.None); }
        public static Snapshot Read(OfficialInstall official, PrivateStore store, Profile profile, CancellationToken cancellation, string sqliteHome = null) {
            cancellation.ThrowIfCancellationRequested();
            string home = store.HomeFor(profile); Safe.PlainPath(home); if (!Directory.Exists(home)) throw new IOException();
            var start = new ProcessStartInfo { FileName = official.Cli, Arguments = "app-server", WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            Safe.CleanEnvironment(start, home, sqliteHome);
            using (var helper = official.StartVerified(start)) {
                using (cancellation.Register(() => { try { if (!helper.HasExited) helper.Kill(); } catch { } })) {
                var errors = helper.StandardError.ReadToEndAsync();
                try {
                    Rpc(helper, 0, "initialize", new { clientInfo = new { name = "desktop_account_panel", title = "Local account panel", version = "0.6.13" } }, cancellation);
                    helper.StandardInput.WriteLine("{\"method\":\"initialized\",\"params\":{}}"); helper.StandardInput.Flush();
                    object account = Safe.Field(Rpc(helper, 1, "account/read", new { refreshToken = false }, cancellation), "account");
                    if ((Safe.Field(account, "type") as string) != "chatgpt") throw new IOException();
                    object result = Rpc(helper, 2, "account/rateLimits/read", new { }, cancellation);
                    var buckets = Safe.Map(Safe.Field(result, "rateLimitsByLimitId")).Values.ToList();
                    if (buckets.Count == 0 && Safe.Field(result, "rateLimits") != null) buckets.Add(Safe.Field(result, "rateLimits"));
                    var snap = new Snapshot { SchemaVersion = 2, Profile = profile.Id, Email = Safe.Text(Safe.Field(account, "email"), 254) ?? "", PlanType = Safe.Text(Safe.Field(account, "planType"), 60) ?? "未知", UpdatedAtUtc = DateTime.UtcNow.ToString("o"), Buckets = buckets.Take(30).Select(b => new Bucket { LimitId = Safe.Text(Safe.Field(b, "limitId"), 80), LimitName = Safe.Text(Safe.Field(b, "limitName"), 80), Primary = Window(Safe.Field(b, "primary")), Secondary = Window(Safe.Field(b, "secondary")) }).ToList() };
                    cancellation.ThrowIfCancellationRequested(); store.SaveSnapshot(profile, snap); return snap;
                } finally {
                    try { helper.StandardInput.Close(); if (!helper.WaitForExit(5000)) { helper.Kill(); helper.WaitForExit(1500); } } catch { }
                    // Only this short-lived, owned status helper is cleaned up. Never ChatGPT.exe.
                }
                }
            }
        }
    }
    public static class Program {
        static int RequestExit() {
            string who = WindowsIdentity.GetCurrent().User.Value;
            try {
                using (var signal = EventWaitHandle.OpenExisting("Local\\ChatGPTDesktopAccountPanel.Exit." + who)) {
                    signal.Set();
                    using (var mutex = Mutex.OpenExisting("Local\\ChatGPTDesktopAccountPanel." + who)) {
                        bool held = false;
                        try { try { held = mutex.WaitOne(12000); } catch (AbandonedMutexException) { held = true; } return held ? 0 : 1; }
                        finally { if (held) mutex.ReleaseMutex(); }
                    }
                }
            } catch (WaitHandleCannotBeOpenedException) { return 0; } catch { return 1; }
        }
        [STAThread] public static void Main(string[] args) {
            if (args.Length == 1 && args[0] == "--request-exit") { Environment.ExitCode = RequestExit(); return; }
            if (args.Length == 2 && args[0] == "--configure-startup" && (args[1] == "0" || args[1] == "1")) { try { StartupRegistration.SetEnabled(args[1] == "1"); } catch { Environment.ExitCode = 1; } return; }
            if (args.Length == 1 && args[0] == "--remove-startup-if-current") { try { StartupRegistration.SetEnabled(false); } catch { Environment.ExitCode = 1; } return; }
            bool background = args.Length == 1 && args[0] == "--background";
            bool created; string who = WindowsIdentity.GetCurrent().User.Value;
            using (var mutex = new Mutex(true, "Local\\ChatGPTDesktopAccountPanel." + who, out created))
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ChatGPTDesktopAccountPanel.Show." + who)) {
                if (!created) { if (!background) signal.Set(); return; }
                try {
                    var storage = StartupStorage.Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chatgpt-work-switcher"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GPTSwitcher", "RecoveryData"));
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var dashboard = new Dashboard(storage.Store, storage.Settings, false, storageProblem: storage.Problem);
                    using (var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ChatGPTDesktopAccountPanel.Exit." + who)) {
                        var thread = new Thread(new ThreadStart(delegate { try { while (!app.Dispatcher.HasShutdownStarted) { int selected = WaitHandle.WaitAny(new WaitHandle[] { signal, exitSignal }); if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new Action(selected == 0 ? (Action)dashboard.OpenFromExecutable : dashboard.ExitFromInstaller)); } } catch (ObjectDisposedException) { } })) { IsBackground = true }; thread.Start();
                        if (background) dashboard.StartFromWindows(); else dashboard.OpenFromExecutable(); app.Run();
                    }
                } catch { MessageBox.Show(new UiText("en")["startupError"], new UiText("en")["panel"]); }
            }
        }
    }
}
