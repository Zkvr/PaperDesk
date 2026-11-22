<p align="center">
  <img src="media/paperdesk-hero.png" alt="PaperDesk for Windows. A yellow weekend checklist settles over a pink studio note." width="100%">
</p>

<p align="center">
  <a href="https://github.com/Zkvr/PaperDesk/releases/tag/v0.9.1-beta"><strong>Download the Windows preview</strong></a>
  &nbsp;·&nbsp; <a href="https://github.com/Zkvr/PaperDesk/releases/tag/v0.9.1-beta#whats-new">What's new</a>
  &nbsp;·&nbsp; <a href="#start-writing">Quick start</a>
</p>

PaperDesk puts checklists, notes, and project stacks on your Windows desktop. It works offline without an account.

## From the manager to the desktop

Pick up a note card in the manager, drag it onto an open spot on the Windows desktop, and let go.

<img src="media/move-to-desktop.gif" width="360" alt="The cursor drags a checklist from the manager onto the desktop.">

## Desktop notes and stacks

### Put a sheet on top

Use the upper grip to move a note. Drop it over another note or stack. The incoming sheet becomes **1 of 3** here, with the previous top sheet directly below.

<img src="media/place-on-stack.gif" width="360" alt="The yellow checklist lands above two sheets in Fieldwork.">

### Peel one sheet free

Grab the lower edge. A short sideways pull bends the corner while the top holds. Pull back toward the top edge to free the sheet sooner.

<img src="media/peel-two-directions.gif" width="360" alt="A sideways tug lifts the corner; pulling upward peels the note free.">

### Move it to another stack

Carry the peeled sheet to a second stack. It becomes the first sheet there too.

<img src="media/move-between-stacks.gif" width="360" alt="The cursor carries the checklist from Fieldwork to the top of Weekend.">

### Discard the top sheet

Click the trash icon. The upper note crumples, revealing the note just beneath it.

<img src="media/discard-top-sheet.gif" width="360" alt="The yellow checklist crumples, revealing Weekend ideas.">

## Themes and paper

Switch between Graphite, Ocean, and Clay in **Settings → Appearance**. The theme applies immediately to the manager.

![PaperDesk manager in the Ocean theme with pastel paper cards and a checklist.](media/manager-ocean.png)

Click the color dot on a sheet to choose its color, texture, corners, shadow, transparency, and size.

<p align="center">
  <img src="media/paper-appearance.png" width="520" alt="A compact desktop note with the paper appearance panel open.">
</p>

## Write and organize

- Edit checklists, tables, and spoilers on the paper. Paste or drag in an image and choose inline, wrap, or overlay placement.
- Find a note with **Ctrl+F** and open it with **Enter**. Keep favorites at the top and move several notes into a stack together.
- Export notes, images, and desktop stacks to a JSON backup you can restore on another PC.

## Start writing

PaperDesk runs on Windows 10 build 19041 or newer and Windows 11 (x64). Both packages include .NET. The app follows the Windows display language for English and Russian.

| Download | Open |
| --- | --- |
| [Installer](https://github.com/Zkvr/PaperDesk/releases/download/v0.9.1-beta/PaperDesk-Setup-win-x64.exe) | Run setup to add PaperDesk to Windows. |
| [Portable ZIP](https://github.com/Zkvr/PaperDesk/releases/download/v0.9.1-beta/PaperDesk-Portable-win-x64.zip) | Extract to a writable folder and open `PaperDesk.exe`. |

Use **Ctrl+Alt+N** for a new desktop note and **Ctrl+Alt+Space** to bring your notes forward. Press **F1** in the manager for shortcuts and stack gestures.

[Installation and backups](docs/USER-GUIDE.md) · [SHA-256 checksums](https://github.com/Zkvr/PaperDesk/releases/download/v0.9.1-beta/SHA256SUMS.txt) · [Changelog](CHANGELOG.md)

## Build from source

On Windows, install the .NET SDK specified in `global.json` and run `dotnet build PaperDesk.sln -c Release`. To run the built app, use `dotnet run --project PaperDesk.Desktop -c Release`.

The Windows editor uses Tiptap and ProseMirror inside Microsoft Edge WebView2. The editor bundle ships with the application and works offline. On PCs without the WebView2 Runtime, PaperDesk opens the built-in Windows editor. To rebuild the editor bundle after changing it, run `npm ci` and `npm run build` in `PaperDesk.Desktop/WebEditor`.

[Packaging and release process](docs/RELEASING.md)

