# PaperDesk for Windows

[Русская инструкция](USER-GUIDE.ru.md)

PaperDesk keeps sticky notes on your desktop. The Windows x64 packages include .NET. The installer accepts Windows 10 build 19041 and later. This release candidate was tested on Windows 11 x64. The built-in editor opens automatically if the Microsoft Edge WebView2 Runtime is unavailable.

## Install and update

1. Choose a version on the [PaperDesk Releases page](https://github.com/Zkvr/PaperDesk/releases). Download the installer or portable ZIP and its `SHA256SUMS.txt`. Preview and RC builds carry the GitHub **Pre-release** label.
2. Compare the downloaded file’s SHA-256 with the entry for its filename. For the installer, run `Get-FileHash -Algorithm SHA256 -LiteralPath .\PaperDesk-Setup-win-x64.exe` in PowerShell. For the ZIP, use its filename instead.
3. Run `PaperDesk-Setup-win-x64.exe` to install PaperDesk for your Windows user. For portable use, extract the entire ZIP to a writable folder and start `PaperDesk.exe` there.

Before updating, make a JSON backup and choose **Exit** from the PaperDesk tray icon. For an installed copy, run the newer installer into the current installation folder. For a portable copy, extract the new ZIP to a separate folder and copy the old `data` folder beside the new `PaperDesk.exe` while both copies are closed. Open the new copy and check that your notes are present.

Remove the installed app through **Settings → Apps → Installed apps**. After reinstalling, PaperDesk opens the notes in your Windows profile. A portable copy keeps its notes in its own folder, so save a backup or copy `data` before deleting that folder.

## Use, back up and restore notes

- **Ctrl+Alt+N** creates a desktop note. **Ctrl+Alt+Space** brings notes forward. In the manager, **Ctrl+F** searches notes and **F1** shows shortcuts and stack gestures.
- Click the paper-colored dot on a note to change its look. Open **Settings → Appearance** to switch the app theme.
- In the manager, choose **File → Export a backup…** and save a **JSON backup**. It includes notes, attachments, archive and trash, stacks, and per-note appearance. Markdown is available as a reading copy of the text.
- To restore, open **File → Import notes…** and select the JSON backup. On a populated profile, a note with a matching ID is imported as a separate copy. An empty profile is appropriate for restoring a whole library.
- To preserve application-wide settings along with notes, exit PaperDesk from the tray and copy the entire `data` folder. **Settings** shows the active path. For a full restore, exit PaperDesk and place the copied folder at that path. Keep a copy of the current folder until the restored notes open correctly.

## Local storage and privacy

The GitHub Windows edition saves your notes, attachments and preferences on the computer. You can use it offline without an account. An installed copy uses `%LOCALAPPDATA%\PaperDesk\data`. A portable copy uses `data` beside `PaperDesk.exe`.

JSON backups and copies of `data` can contain the full text and images of your notes. Store them where you control access. The local troubleshooting log records error categories and codes. Windows manages its own WebView2 Runtime updates.
