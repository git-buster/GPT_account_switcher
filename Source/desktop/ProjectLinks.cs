using System;
using System.Diagnostics;

namespace DesktopAccounts {
    public enum ProjectLink { Repository, Feedback }
    public static class ProjectLinks {
        public const string Author = "git-buster";
        public static string Address(ProjectLink link) {
            switch (link) {
                case ProjectLink.Repository: return "https://github.com/git-buster/GPT_account_switcher";
                case ProjectLink.Feedback: return "https://github.com/git-buster/GPT_account_switcher/issues/new";
                default: throw new ArgumentOutOfRangeException("link");
            }
        }
        public static void Open(ProjectLink link) {
            // Fixed public URLs only. No account details, paths or diagnostics are appended.
            var started = Process.Start(new ProcessStartInfo { FileName = Address(link), UseShellExecute = true });
            if (started != null) started.Dispose();
        }
    }
}
