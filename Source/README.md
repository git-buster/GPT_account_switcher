# GPT Switcher for Windows

Author: **git-buster** · Version: **0.6.14** · MIT License

[Project](https://github.com/git-buster/GPT_account_switcher) · [Portable ZIP](https://github.com/git-buster/GPT_account_switcher/releases/latest/download/ChatGPTDesktopSwitcher-Windows.zip) · [MSI installer](https://github.com/git-buster/GPT_account_switcher/releases/latest/download/ChatGPTDesktopSwitcher-Setup.msi) · [Feedback](https://github.com/git-buster/GPT_account_switcher/issues/new)

[English](docs/USAGE.md) · [简体中文](docs/README.zh-Hans.md) · [繁體中文](docs/README.zh-Hant.md) · [Français](docs/README.fr.md) · [日本語](docs/README.ja.md)

A compact Windows tray panel for opening and restoring multiple official ChatGPT desktop accounts, viewing usage snapshots and organizing shared local Codex history. **English is the default**, with Simplified Chinese, Traditional Chinese, French and Japanese available in settings.

<img src="docs/images/panel-en.png" width="350" alt="Account panel with fictional accounts">

## Install or run

- **Portable:** extract the ZIP, then run **ChatGPTAccountSwitcher.exe**. Keep the accompanying configuration file beside it.
- **Installer:** run **ChatGPTDesktopSwitcher-Setup.msi**. It installs for the current Windows user and creates desktop and Start menu shortcuts. Windows startup is optional and initially off.
- Quit an already-running switcher before updating or opening another copy. Re-launching an EXE otherwise activates the copy already running.

Requires Windows 10/11, .NET Framework 4.8 and one complete supported official ChatGPT/Codex desktop installation. Shared local history needs a local disk supporting directory junctions, normally NTFS. Additional accounts do not require additional official client installations.

## Main features

- Add and name multiple accounts, then sign in through each account's official window.
- Click an account to restore its window. For an unopened account, choose to keep existing clients open or explicitly close them first.
- View the plan, remaining Work/Codex usage and reset times returned by the official helper. These snapshots do not cover every ChatGPT feature's allowance.
- Refresh on an EXE launch, including re-launching it while already running. Tray-panel opens do not refresh. Optional intervals: 5, 10 or 30 minutes; 1 or 2 hours; Never. The last successful refresh time appears at the lower right.
- Save or manually select the complete official client path, including supported desktop shortcuts. **If opening fails after an update or move, use Accounts and settings → Auto detect → Save settings.**
- Use **Organize local history** to close supported clients, check local histories, enable one common local history and update local project entries. Newly added empty accounts then attach automatically.
- Switch between common and independent history through the organization page. Independent mode keeps existing common records in the default account.

## Organize and continue local work

Wait for important tasks to finish. Choose **Organize local history → Use one shared local history → Close apps and organize**. Confirmation closes all supported official clients in the current Windows session, including open chats, and interrupts remaining tasks. The switcher remains running. Do not reopen clients until the operation finishes.

<img src="docs/images/history-en.png" width="350" alt="Explicit organization confirmation">

After completion, open your accounts again. A and B can work simultaneously on **different chats**. Before continuing **the same chat** in B, use **File → Quit** in A, then reopen the chat in B. Stopping a turn or clicking × can leave the official writer lock held. Running organization again also closes both clients first. An unfinished running model operation is not transferred.

Histories use a common source rather than complete copies per account. The latest organization keeps a recovery backup; a later successful operation cleans older marked backups. Conflicting versions or missing source records stop organization and retain recovery information. Existing windows may need to reopen a chat to load newer items. This feature covers local work.

## Build and validate

From the source package, run in Windows PowerShell:

```powershell
./build-desktop.ps1
./build-checks.ps1
& ./dist/SwitcherChecks.exe --self-test ./test-output
./validate-source.ps1
./build-installer.ps1
python ./validate-installer.py ./release/ChatGPTDesktopSwitcher-0.6.14-Setup.msi --isolated-install
./package-desktop.ps1
```

The build uses Windows' installed .NET Framework compiler, cabinet tools and Windows Installer. Python is needed for the separate MSI validator. Its optional isolated install uses random test identities, temporary directories and dedicated registry entries for install, repair and uninstall; it does not install over the normal product. Packaging produces **Source/**, **Portable/** and **Installer/**, with a source ZIP, portable ZIP, MSI and checksums. Existing package outputs are not overwritten.

See the illustrated guide in your language and [Security](SECURITY.md). This project is independent of OpenAI. Use only accounts you are authorized to access and follow the applicable service terms.

For a supported registered official installation, the switcher follows a replacement package from the same verified application family after an update and saves its new path. An unchanged client path does not prevent saving language or refresh preferences. A newly selected path must still pass validation. If automatic recovery fails, use **Auto detect → Save settings**.

## Recovery and uninstall

The panel opens even when original account settings are unavailable. Recovery mode explains the cause and, when possible, offers a separate private workspace for new sign-ins; history organization is disabled until original access is restored. Restart the current version after fixing access. Administrator mode does not repair an incompatible settings format.

The MSI adds **Uninstall GPT Switcher** to the Start menu. Windows Apps settings also supports uninstall. Installed program files, guides, shortcuts and the switcher’s own startup entry are removed; official clients, sign-ins and working history are retained. Wait for any organization to finish first.
