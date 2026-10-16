using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DesktopAccounts {
    public sealed class ClientInstallationChangedException : IOException {
        public readonly string Family;
        public ClientInstallationChangedException(string family) : base("Saved installation changed. Use Auto detect.") { Family = family; }
    }
    public static class ClientResolutionPolicy {
        public static bool SameSelection(string first, string second) { return String.Equals((first ?? "").Trim(), (second ?? "").Trim(), StringComparison.OrdinalIgnoreCase); }
        public static bool NeedsValidation(string selected, string saved) { return !String.IsNullOrWhiteSpace(selected) && !SameSelection(selected, saved); }
        public static OfficialInstall ResolveSaved(string saved, Func<string, OfficialInstall> resolve, Func<OfficialInstall> detect) {
            try { return resolve(saved); }
            catch (ClientInstallationChangedException changed) {
                var current = detect();
                if (current == null || String.IsNullOrEmpty(changed.Family) || !String.Equals(current.Family, changed.Family, StringComparison.Ordinal)) throw new IOException("Detected application differs from the saved package family.");
                return current;
            }
        }
        public static bool Remember(Settings settings, OfficialInstall current, Action<Settings> persist) {
            if (SameSelection(settings.AppExePath, current.Exe)) return true;
            settings.AppExePath = current.Exe;
            try { persist(settings); return true; } catch { return false; }
        }
    }
    // Inspect only an already verified local process's profile selector. No titles,
    // credentials, raw command lines or environment blocks are stored or logged.
    public static class ClientLaunchIdentity {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CommandLineToArgvW(string value, out int count);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr value);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder name);
        public static bool MatchesArguments(string[] args, string directory) {
            if (args == null || args.Length == 0 || String.IsNullOrWhiteSpace(args[0])) return false;
            string selected = null; bool seen = false;
            for (int i = 1; i < args.Length; i++) {
                string argument = args[i]; if (argument == null) return false;
                if (argument.Equals("--type", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("--type=", StringComparison.OrdinalIgnoreCase)) return false;
                if (!argument.StartsWith("--user-data-dir", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen) return false; seen = true;
                if (argument.Equals("--user-data-dir", StringComparison.OrdinalIgnoreCase)) { if (++i >= args.Length) return false; selected = args[i]; }
                else if (argument.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase)) selected = argument.Substring("--user-data-dir=".Length);
                else return false;
                if (String.IsNullOrWhiteSpace(selected) || !Path.IsPathRooted(selected) || selected.Any(Char.IsControl)) return false;
            }
            if (directory == null) return !seen;
            if (!seen) return false;
            try { return String.Equals(Path.GetFullPath(selected).TrimEnd('\\'), Path.GetFullPath(directory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        public static string[] Arguments(int id) {
            if (id <= 0) return null;
            try {
                using (var process = new ManagementObject("Win32_Process.Handle='" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "'")) {
                    process.Get(); string command = process["CommandLine"] as string;
                    if (String.IsNullOrWhiteSpace(command) || command.Length > 32767) return null;
                    int count; IntPtr values = CommandLineToArgvW(command, out count);
                    if (values == IntPtr.Zero) return null;
                    try {
                        if (count <= 0 || count > 512) return null;
                        var result = new string[count]; for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(values, i * IntPtr.Size)); return result;
                    } finally { LocalFree(values); }
                }
            } catch { return null; }
        }
        public static string PackageFamily(int id) {
            IntPtr process = OpenProcess(0x1000, false, id); if (process == IntPtr.Zero) return null;
            try {
                uint size = 0; if (GetPackageFamilyName(process, ref size, null) != 122 || size == 0 || size > 1024) return null;
                var value = new StringBuilder((int)size); return GetPackageFamilyName(process, ref size, value) == 0 ? value.ToString() : null;
            } finally { CloseHandle(process); }
        }
        public static bool HasPackageIdentity(int id, string family) { string actual = PackageFamily(id); return actual != null && (String.IsNullOrEmpty(family) || String.Equals(actual, family, StringComparison.OrdinalIgnoreCase)); }
        public static AppProcess Choose(IEnumerable<AppProcess> matches, bool defaultProfile, Func<AppProcess, bool> packaged) {
            var candidates = matches.ToArray(); if (defaultProfile) { var officialEntry = candidates.Where(packaged).ToArray(); if (officialEntry.Length == 1) return officialEntry[0]; if (officialEntry.Length > 1) return null; }
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }
    public static class OfficialActivation {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int PackageFamilyNameFromFullName(string fullName, ref uint size, StringBuilder family);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint size, IntPtr buffer);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackagePathByFullName(string fullName, ref uint size, StringBuilder path);
        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] class ActivationManager { }
        [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IActivationManager {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint processId);
        }
        public static string FamilyFromExecutable(string exe) {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(exe))), fullName = Path.GetFileName(root);
            uint length = 0;
            if (PackageFamilyNameFromFullName(fullName, ref length, null) != 122 || length == 0 || length > 1024) return null;
            var family = new StringBuilder((int)length); if (PackageFamilyNameFromFullName(fullName, ref length, family) != 0 || !family.ToString().StartsWith("OpenAI.Codex_", StringComparison.Ordinal)) return null;
            return family.ToString();
        }
        public static string RegisteredFamily(string exe) {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(exe))), fullName = Path.GetFileName(root), family = FamilyFromExecutable(exe);
            if (family == null) return null;
            uint count = 0, size = 0; int result = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref size, IntPtr.Zero);
            if (result != 122 || count == 0 || count > 256 || size == 0 || size > 1048576) throw new ClientInstallationChangedException(family);
            IntPtr names = Marshal.AllocHGlobal((int)count * IntPtr.Size), buffer = Marshal.AllocHGlobal((int)size * 2);
            try {
                if (GetPackagesByPackageFamily(family, ref count, names, ref size, buffer) != 0) throw new IOException("Registered application could not be checked.");
                var main = new List<string>();
                for (int i = 0; i < count; i++) { string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size)); var parts = name.Split('_'); if (parts.Length == 5 && parts[3] == "") main.Add(name); }
                if (main.Count != 1 || !String.Equals(main[0], fullName, StringComparison.OrdinalIgnoreCase)) throw new ClientInstallationChangedException(family);
            } finally { Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(buffer); }
            uint length = 0; if (GetPackagePathByFullName(fullName, ref length, null) != 122 || length == 0 || length > 32768) throw new ClientInstallationChangedException(family);
            var installed = new StringBuilder((int)length); if (GetPackagePathByFullName(fullName, ref length, installed) != 0 || !String.Equals(Path.GetFullPath(installed.ToString()).TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new IOException("Selected application differs from the registered installation.");
            return family;
        }
        public static string ApplicationId(string xml, string packageRoot, string selectedExe, string family) {
            if (xml == null || xml.Length > 1048576 || family == null || !Regex.IsMatch(family, @"\AOpenAI\.Codex_[a-z0-9]{13}\z")) throw new IOException("Unsupported registered application.");
            var document = new XmlDocument { XmlResolver = null };
            using (var input = new StringReader(xml)) using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 })) document.Load(reader);
            string root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, selected = Path.GetFullPath(selectedExe); var matches = new List<string>();
            foreach (XmlElement application in document.GetElementsByTagName("Application")) {
                string exe = application.GetAttribute("Executable"), id = application.GetAttribute("Id");
                if (String.IsNullOrWhiteSpace(exe) || Path.IsPathRooted(exe) || exe.Contains(':') || exe.Any(Char.IsControl)) continue;
                string full = Path.GetFullPath(Path.Combine(root, exe));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !String.Equals(full, selected, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Regex.IsMatch(id, @"\A[A-Za-z0-9._-]{1,100}\z")) throw new IOException("Invalid registered application identifier.");
                matches.Add(family + "!" + id);
            }
            if (matches.Count != 1) throw new IOException("Registered application identity is ambiguous.");
            return matches[0];
        }
        public static string ReadApplicationId(string exe, string family) {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(exe)), manifest = Path.Combine(root, "AppxManifest.xml"); Safe.PlainPath(manifest);
            using (var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (stream.Length > 1048576) throw new IOException("Registered application manifest is too large.");
                using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return ApplicationId(reader.ReadToEnd(), root, exe, family);
            }
        }
        public static Process Start(string id) {
            var manager = (IActivationManager)new ActivationManager();
            try {
                uint pid; int result = manager.ActivateApplication(id, null, 2, out pid);
                if (result < 0 || pid == 0 || pid > Int32.MaxValue) throw new IOException("Official registered launch failed.");
                try { return Process.GetProcessById((int)pid); } catch (ArgumentException) { return null; }
            } finally { Marshal.ReleaseComObject(manager); }
        }
    }
    // Windows owns these display strings; no HICON handles, credentials or client
    // files are replaced. A pinned independent window reopens the account selector.
    public static class TaskbarPresentation {
        [StructLayout(LayoutKind.Sequential)] struct Key { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] struct Value { [FieldOffset(0)] public ushort Kind; [FieldOffset(8)] public IntPtr Data; }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IStore {
            void GetCount(out uint count); void GetAt(uint index, out Key key); void GetValue(ref Key key, out Value value);
            void SetValue(ref Key key, ref Value value); void Commit();
        }
        [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IStore store);
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref Value value);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        static Key Property(uint id) { return new Key { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id }; }
        static string Read(IStore store, uint id) { var key = Property(id); Value value; store.GetValue(ref key, out value); try { return value.Kind == 31 ? Marshal.PtrToStringUni(value.Data) : null; } finally { PropVariantClear(ref value); } }
        static void Write(IStore store, uint id, string text) {
            var key = Property(id); var value = new Value { Kind = 31, Data = Marshal.StringToCoTaskMemUni(text) };
            try { store.SetValue(ref key, ref value); } finally { PropVariantClear(ref value); }
        }
        public static string IconResource(string image) { OfficialInstall.ValidateCandidatePath(image); return Path.GetFullPath(image) + ",0"; }
        public static string SelectorCommand(string launcher) { OfficialInstall.ValidateLocalPath(launcher); return "\"" + Path.GetFullPath(launcher) + "\""; }
        public static string IndependentId(string profile) { if (!Safe.Id(profile)) throw new IOException("Invalid profile."); return "GPTSwitcher.ChatGPT." + profile; }
        public static bool Ensure(IntPtr window, AppProcess owner) {
            if (window == IntPtr.Zero || owner == null || !Safe.Id(owner.ProfileId) || !String.Equals(Path.GetFileName(owner.Image), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
            try {
                uint pid; GetWindowThreadProcessId(window, out pid); if (pid != owner.Id) return false;
                using (var process = Process.GetProcessById(owner.Id)) using (var self = Process.GetCurrentProcess()) {
                    if (process.HasExited || !ClientWindows.SameOwner(owner, new AppProcess { Id = process.Id, SessionId = process.SessionId, Started = process.StartTime.ToUniversalTime().ToFileTimeUtc(), Image = process.MainModule.FileName }, self.SessionId)) return false;
                }
                string registered = null;
                if (owner.ProfileId == "Default") { string family = ClientLaunchIdentity.PackageFamily(owner.Id); if (family != null) registered = OfficialActivation.ReadApplicationId(owner.Image, family); }
                return Apply(window, owner.Image, owner.ProfileId, registered, Assembly.GetExecutingAssembly().Location);
            } catch { return false; }
        }
        static bool Apply(IntPtr window, string image, string profile, string registered, string launcher) {
            IStore store = null;
            try {
                string resource = IconResource(image); var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                if (SHGetPropertyStoreForWindow(window, ref iid, out store) != 0) return false;
                string existing = Read(store, 5), desired = existing;
                if (String.IsNullOrEmpty(desired)) {
                    desired = registered ?? IndependentId(profile);
                }
                bool changed = false;
                if (Read(store, 3) != resource) { Write(store, 3, resource); changed = true; }
                // An independent account must not be relaunched as the default login by a
                // pinned button. Open the selector instead, with no private arguments.
                if (String.IsNullOrEmpty(existing) && desired.StartsWith("GPTSwitcher.ChatGPT.", StringComparison.Ordinal)) {
                    Write(store, 2, SelectorCommand(launcher)); Write(store, 4, "ChatGPT"); changed = true;
                }
                if (existing != desired) { Write(store, 5, desired); changed = true; }
                if (changed) store.Commit(); return true;
            } catch { return false; } finally { if (store != null) Marshal.ReleaseComObject(store); }
        }
        public static bool HasIconResource(IntPtr window, string image) {
            IStore store = null; try { var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"); return SHGetPropertyStoreForWindow(window, ref iid, out store) == 0 && Read(store, 3) == IconResource(image); } catch { return false; } finally { if (store != null) Marshal.ReleaseComObject(store); }
        }
    }
}
