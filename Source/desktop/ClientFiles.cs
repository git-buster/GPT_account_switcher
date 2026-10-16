using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace DesktopAccounts {
    public static class StartupRegistration {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "GPTSwitcher";
        static string Executable { get { return System.Reflection.Assembly.GetExecutingAssembly().Location; } }
        public static string Command(string executable) { OfficialInstall.ValidateLocalPath(executable); return "\"" + Path.GetFullPath(executable) + "\" --background"; }
        public static bool Matches(string registered, string executable) { return String.Equals(registered, Command(executable), StringComparison.OrdinalIgnoreCase); }
        public static string Capture() { using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, false)) return key == null ? null : key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string; }
        public static bool IsEnabled() { return Matches(Capture(), Executable); }
        public static void SetEnabled(bool enabled) {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath)) {
                if (enabled) key.SetValue(ValueName, Command(Executable), RegistryValueKind.String);
                else if (Matches(key.GetValue(ValueName) as string, Executable)) key.DeleteValue(ValueName, false);
            }
        }
        public static void Rollback(string expected, string previous) {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath)) if (String.Equals(key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, expected, StringComparison.Ordinal)) {
                if (previous == null) key.DeleteValue(ValueName, false); else key.SetValue(ValueName, previous, RegistryValueKind.String);
            }
        }
        public static string CurrentCommand { get { return Command(Executable); } }
    }
    // Only reads shortcut metadata. Never resolves, repairs or executes a link.
    public static class ClientShortcut {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLink {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length, IntPtr findData, uint flags);
            void GetIDList(out IntPtr value);
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] static extern int SHGetNameFromIDList(IntPtr id, uint kind, out IntPtr value);
        public static bool MatchesStoreApp(string value, string family) {
            if (String.IsNullOrWhiteSpace(value) || String.IsNullOrWhiteSpace(family)) return false;
            foreach (string prefix in new[] { "shell:AppsFolder\\", "::{4234d49b-0245-4df3-b780-3893943456e1}\\" }) {
                string expected = prefix + family + "!";
                if (value.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) {
                    string id = value.Substring(expected.Length); return id.Length > 0 && id.Length <= 100 && System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9._-]+$");
                }
            }
            return false;
        }
        public static string Read(string path, Func<string, string> storeApp) {
            if (!String.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose an executable or a supported shortcut.");
            OfficialInstall.ValidateLocalPath(path); var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1048576) throw new IOException("Shortcut is unavailable.");
            object link = null; IntPtr id = IntPtr.Zero, name = IntPtr.Zero;
            try {
                link = new ShellLink(); ((IPersistFile)link).Load(path, 0);
                var shell = (IShellLink)link; var target = new StringBuilder(2049); shell.GetPath(target, target.Capacity, IntPtr.Zero, 4);
                if (target.Length > 0) {
                    string value = Environment.ExpandEnvironmentVariables(target.ToString());
                    if (Path.IsPathRooted(value) && !value.StartsWith("::{", StringComparison.Ordinal)) return value;
                    string mapped = storeApp(value); if (mapped != null) return mapped;
                    throw new IOException("Unsupported shortcut target.");
                }
                shell.GetIDList(out id);
                if (id != IntPtr.Zero && SHGetNameFromIDList(id, 0x80028000, out name) == 0 && name != IntPtr.Zero) {
                    string mapped = storeApp(Marshal.PtrToStringUni(name)); if (mapped != null) return mapped;
                }
                throw new IOException("Choose ChatGPT.exe directly or use automatic detection.");
            } finally { if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name); if (id != IntPtr.Zero) Marshal.FreeCoTaskMem(id); if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link); }
        }
    }
    // Keep both verified files read-locked until the new process has been created.
    public sealed class ClientBundleFiles : IDisposable {
        readonly FileStream exe, cli;
        public readonly string ExeHash, CliHash;
        static string Hash(Stream input) { using (var hash = SHA256.Create()) { string value = Convert.ToBase64String(hash.ComputeHash(input)); input.Position = 0; return value; } }
        public ClientBundleFiles(string executable, string helper) {
            Safe.PlainPath(executable); Safe.PlainPath(helper);
            try { exe = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read); cli = new FileStream(helper, FileMode.Open, FileAccess.Read, FileShare.Read); ExeHash = Hash(exe); CliHash = Hash(cli); }
            catch { Dispose(); throw; }
        }
        public bool Matches(string executableHash, string helperHash) { return ExeHash == executableHash && CliHash == helperHash; }
        public void Dispose() { if (cli != null) cli.Dispose(); if (exe != null) exe.Dispose(); }
    }
    // One action can start several official helpers. Hold the same validated files
    // read-locked for that action instead of hashing them again before every helper.
    public sealed class ClientBundleGuard {
        readonly object gate = new object(); readonly string exe, cli, exeHash, cliHash;
        ClientBundleFiles files; int users;
        public int VerificationCount { get; private set; }
        public ClientBundleGuard(string executable, string helper, string executableHash, string helperHash) { exe = executable; cli = helper; exeHash = executableHash; cliHash = helperHash; }
        public IDisposable Acquire() {
            lock (gate) {
                if (files == null) {
                    var opened = new ClientBundleFiles(exe, cli);
                    if (!opened.Matches(exeHash, cliHash)) { opened.Dispose(); throw new IOException("Client changed after verification."); }
                    files = opened; VerificationCount++;
                }
                users++; return new Lease(this);
            }
        }
        void Release() { lock (gate) if (--users == 0) { files.Dispose(); files = null; } }
        sealed class Lease : IDisposable {
            ClientBundleGuard owner;
            public Lease(ClientBundleGuard value) { owner = value; }
            public void Dispose() { var previous = Interlocked.Exchange(ref owner, null); if (previous != null) previous.Release(); }
        }
    }
}
