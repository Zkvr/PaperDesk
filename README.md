<p align="center">
  <img src="media/paperdesk-hero.png" alt="PaperDesk. Sticky notes that feel like paper. Three pastel sheets on a graphite desk, with a handwritten checklist on top." width="100%">
</p>

<p align="center">
  <a href="https://github.com/Zkvr/PaperDesk/releases/tag/v0.8.0-beta"><strong>Download v0.8.0-beta</strong></a>
  &nbsp;·&nbsp; <a href="https://github.com/Zkvr/PaperDesk/releases/tag/v0.8.0-beta#whats-new">What's new</a>
  &nbsp;·&nbsp; <a href="#start-writing">Quick start</a>
</p>

PaperDesk puts checklists, notes, and project stacks on your Windows desktop. It works offline without an account.

## Themes and paper

Switch between Graphite, Ocean, and Clay in **Settings → Appearance**. The theme applies immediately to the manager.

![PaperDesk manager in the Ocean theme with pastel paper cards and a checklist.](media/manager-ocean.png)

Click the color dot on a sheet to choose its color, texture, corners, shadow, transparency, and size.

<p align="center">
  <img src="media/paper-appearance.png" width="520" alt="A compact desktop note with the paper appearance panel open.">
</p>

## Desktop notes and stacks

Drag a sheet or stack by its upper grip to combine it with another. Pull the lower edge to peel off one sheet. Turn on **Snap sheets to nearby edges** in **Settings → Gestures and animation** to line up adjacent notes.

![Two named stacks merge, then the top note is peeled off as a separate sheet.](media/stack-and-peel.gif)

<details>
  <summary>See a finished sheet crumple</summary>

  <p align="center">
    <img src="media/paper-motion.gif" width="360" alt="A yellow sheet crumples and reveals the next note in its stack.">
  </p>

</details>

## Write and organize

- Edit checklists, tables, and spoilers on the paper. Paste or drag in an image and choose inline, wrap, or overlay placement.
- Find a note with **Ctrl+F** and open it with **Enter**. Keep favorites at the top and move several notes into a stack together.
- Export notes, images, and desktop stacks to a JSON backup you can restore on another PC.

## Start writing

PaperDesk runs on Windows 10 build 19041 or newer and Windows 11 (x64). Both packages include .NET. The app follows the Windows display language for English and Russian.

| Download | Open |
| --- | --- |
| [Installer](https://github.com/Zkvr/PaperDesk/releases/download/v0.8.0-beta/PaperDesk-Setup-win-x64.exe) | Run setup to add PaperDesk to Windows. |
| [Portable ZIP](https://github.com/Zkvr/PaperDesk/releases/download/v0.8.0-beta/PaperDesk-Portable-win-x64.zip) | Extract to a writable folder and open `PaperDesk.exe`. |

Use **Ctrl+Alt+N** for a new desktop note and **Ctrl+Alt+Space** to bring your notes forward. Press **F1** in the manager for shortcuts and stack gestures.

[Installation and backups](docs/USER-GUIDE.md) · [SHA-256 checksums](https://github.com/Zkvr/PaperDesk/releases/download/v0.8.0-beta/SHA256SUMS.txt) · [Changelog](CHANGELOG.md)

## Build from source

On Windows, install the .NET SDK specified in `global.json` and run `dotnet build PaperDesk.sln -c Release`. To run the built app, use `dotnet run --project PaperDesk.Desktop -c Release`.

The Windows editor uses Tiptap and ProseMirror inside Microsoft Edge WebView2. The editor bundle ships with the application and works offline. On PCs without the WebView2 Runtime, PaperDesk opens the built-in Windows editor. To rebuild the editor bundle after changing it, run `npm ci` and `npm run build` in `PaperDesk.Desktop/WebEditor`.

[Packaging and release process](docs/RELEASING.md)

