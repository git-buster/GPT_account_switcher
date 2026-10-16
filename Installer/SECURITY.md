# Security and privacy

GPT Switcher is a community tool by **git-buster**, independent of OpenAI.

## Credentials and local data

Sign in only through the official client's login flow. The switcher has no password, verification-code, cookie-import or token-entry field. It has no telemetry, upload endpoint or automatic message sending. Quota checks run when the EXE is launched and, optionally, at the saved periodic interval. Tray-panel opens do not trigger quota checks. The periodic timer is initially off. The official client still communicates with OpenAI for normal login, account checks and product use.

Account display names, selected language, optional application path, window bookkeeping and quota snapshots are stored in a private local folder protected for the current Windows user and SYSTEM. The switcher's metadata uses Windows user-bound encryption. This is not a guarantee against software already running as that user or an administrator. Official authentication and browser-profile storage remain the official client's responsibility.

Local history sharing is initially off. Enable or disable it through **Organize local history**, which requires explicit confirmation before closing supported clients. The operation checks local records and retains recovery information; conflicts or missing source records stop the operation. New empty accounts attach automatically after sharing is enabled. Existing independent history requires offline organization. Refer to the [guide](../Source/docs/USAGE.md) for independent mode and backup retention. Sharing local work does not transfer subscription entitlements. Profile separation does not isolate files opened under the same Windows user.

Different chats can run in separate accounts at the same time. To continue the same chat in another account, use **File → Quit** in the previous account first. Stopping a turn or clicking × can leave the official writer lock held. The switcher does not bypass this lock or transfer an unfinished model operation.

## Application and process handling

Automatic detection uses the installed supported official bundle. A manual selection must resolve to the local official ChatGPT.exe with its companion helper. Supported shortcuts are inspected without executing their targets or arguments, and only the resolved verified executable is saved. Executable metadata and supported valid OpenAI signatures are checked before use. Unsupported selections fail without launching them. Cached verification also checks file contents, and both files remain read-locked while their verified process is being created. A changed file must pass verification again. A valid selected path is saved only for this Windows user.

Existing windows are matched with their verified executable and process identity before restoration. Closing clients requires the explicit **Close all, then open** choice or the separate **Close apps and organize** confirmation. It targets the selected supported official installation's processes in the current Windows session and interrupts their tasks. It does not use a generic process-tree kill against the switcher. Keeping apps open does not call global cleanup. Cancelling quota refresh stops only its own temporary helper; it does not close signed-in account windows.

## Reporting an issue

Use the repository's issue tracker with a minimal reproduction and fictional data. Do not post secrets, account data, local histories or unredacted logs. For a vulnerability involving private data, use the repository's private reporting option if available; otherwise describe only the non-sensitive impact until a private channel is established.

Optional Windows startup registers only this copy of GPT Switcher for the current user and starts quietly in the tray. It does not enable startup of any official account window. The installer creates public program shortcuts and preserves account data on uninstall. Its startup helper does not read account stores. Tray placement reads only the switcher’s own notification-icon identity and coordinates; no other application icons are enumerated. File checks can share a validated read lock within one operation; new operations still detect content changes. Taskbar icon associations use Windows-owned display metadata rather than external icon-handle writes.
