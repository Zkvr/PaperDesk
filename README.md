<p align="center">
  <img src="media/paperdesk-hero.png" alt="PaperDesk. Sticky notes that feel like paper. Three pastel sheets on a graphite desk, with a handwritten checklist on top." width="100%">
</p>

<p align="center">
  <a href="https://github.com/Zkvr/PaperDesk/releases"><strong>Windows builds</strong></a>
  &nbsp;·&nbsp; <a href="#a-desk-you-can-arrange">See how it works</a>
  &nbsp;·&nbsp; <a href="#start-writing">Quick start</a>
</p>

Keep a checklist beside your work, a stack for each project, and a blank sheet for your next thought. PaperDesk puts sticky notes on your Windows desktop. Everything stays on your computer, and you can start writing offline without an account.

## A desk you can arrange

![PaperDesk’s Windows manager with pastel notes, desktop sections, and a wide checklist editor with an image attachment](media/manager.png)

- **Peel, drop, stack.** Pull a sheet out of a stack by its lower edge. Drop it onto another note to make a new stack, then give the stack a name.
- **Keep the whole thought.** Edit task labels, table cells, and spoilers in place. Paste a screenshot or drag an image from Chrome or Explorer, then choose whether it sits in the text, wraps the text, or floats on top.
- **Find it when you need it.** Bring notes forward with a shortcut, or search and sort them in the manager. Archive or trash several sheets at once.

<details>
  <summary><strong>Watch a sheet crumple</strong></summary>
  <br>
  <img src="media/paper-motion.gif" width="360" alt="The yellow note crumples, revealing the next sheet in the stack.">
</details>

## Start writing

The preview runs on Windows x64 and follows your display language for English or Russian. The installer and portable ZIP both include .NET.

| Choose | Open |
| --- | --- |
| **Installer** | Run `PaperDesk-Setup-win-x64.exe` to add PaperDesk to Windows. |
| **Portable ZIP** | Extract `PaperDesk-Portable-win-x64.zip` to a writable folder and open `PaperDesk.exe`. |

Use **Ctrl+Alt+N** for a new desktop note and **Ctrl+Alt+Space** to bring your notes forward. In the manager, **Ctrl+V** pastes an image into the selected note. Use **File → Export / backup** to save your notes and images as a JSON file you can move to another PC.

## Build from source

On Windows, install the .NET SDK specified in `global.json` and run `dotnet build PaperDesk.sln -c Release`. To run the built app, use `dotnet run --project PaperDesk.Desktop -c Release`.

The first preview is being prepared. The repository is private while the release and license are being finalized.
