

# Lumen — Spotlight for Windows

Press **Ctrl+Space** anywhere, type, press **Enter**. Lumen is one small `Lumen.exe` built on the .NET Framework that
ships with Windows, so there's nothing to install alongside it. It uses Windows 11 acrylic, follows your light/dark
mode and accent color, and opens on the monitor your mouse is on.

## What it finds

| Type | Examples |
|---|---|
| Apps (every app in Start, including Store apps) | `exc`, `vsc` (initials), `finder` (aliases) |
| **App commands** | `excel - new`, `code - code`, `chrome - incognito` (see below) |
| Files & folders (Windows Search index, word-prefix matching like Spotlight) | `budget`, `q3 report` |
| Text inside documents | rows marked *Contains "…"* |
| Settings pages and admin tools | `bluetooth`, `wifi`, `dark mode`, `env`, `device manager` |
| System actions | `lock`, `sleep`, `restart`, `shut down` (these ask for a second Enter), `empty recycle bin` |
| Open windows | `chrome` also lists open Chrome windows to switch to |
| Calculator & conversions | `2^10/4`, `15% of 80`, `sqrt(2)*pi`, `0xff+1`, `5 km to mi`, `72 f to c`, `2 gb to mb` |
| Web shortcuts | `g cats`, `yt lofi`, `gh repo`, `wiki x`, `maps cafe`; anything else ends with "Search Google for …" |
| URLs & paths | `github.com`, `C:\Users\`, `~\Downloads\` (Tab completes paths) |
| Terminal | `> ipconfig /all` (Ctrl+Shift+Enter runs it as admin) |
| Chrome / Edge bookmarks | bookmark titles and sites |

Lumen learns what you pick: if you choose *Excel* after typing `e`, Excel moves to the top for `e` next time.

## App commands: `app - command [argument]`

Type an app name, then ` - `:

```
excel - new                 new blank workbook
excel - new budget          creates Documents\budget.xlsx and opens it (opens it if it already exists)
excel - recent q3           open a recent workbook (suggestions as you type)
excel - add sheet           add a worksheet to the open workbook
word - new / powerpoint - new / word - recent
code - code                 new coding session: makes ~\Projects\session-<date-time>, opens it in a new VS Code window
code - code my-app          same, named my-app
code - open                 pick a project (VS Code recents + your projects folder)
code - scratch / new / settings
chrome - incognito          (also: new, bookmarks, history, downloads, extensions, settings)
edge - inprivate
explorer - downloads        (documents, desktop, projects, recycle bin, this pc, new folder…)
terminal - admin            (powershell, cmd, here <folder>)
outlook - email bob@x.com   (meeting, task, contact, calendar, inbox — classic Outlook)
notepad - jot buy milk      appends a line to Documents\Quick Notes.txt without opening anything
snip - region               screen snip
media - next                (play, previous, mute, volume up/down)
lumen - commands            edit your commands
```

* `excel -` lists everything Excel can do. **Tab** on an app fills in `excel - `.
* Short forms work: `exc - n`, `vsc - code`, `excel-new`.
* Typing `?` lists every app that has commands.
* You can also type the words in any order: `excel new budget` and `new excel` both find the command.

### Add your own

`lumen - commands` (or the tray icon → *Edit app commands*) opens `%APPDATA%\Lumen\commands.json`.
Saving it applies the change immediately. For example, to add Spotify:

```jsonc
{
  "name": "Spotify", "match": ["spotify"], "icon": "Spotify",
  "commands": [
    { "name": "search", "arg": "song", "argRequired": true, "description": "Search Spotify",
      "action": { "type": "open", "target": "spotify:search:{arg:url}" } }
  ]
}
```

Action types: `run` (program + args, can create a folder first, can run as admin), `open` (URL / file / folder /
`shell:` / `ms-settings:`), `powershell` (script, typed text in `$env:LUMEN_ARG`), `keys` (focus an app and send
keystrokes), `media`, and `builtin`. Placeholders: `{arg}`, `{arg:url}`, `{arg:file}`, `{projects}`, `{documents}`,
`{desktop}`, `{downloads}`, `{date}`, `{datetime}`, `{clipboard}`, plus `%ENV%` variables. Argument suggestions:
`office-recent:Excel`, `folders:{projects}`, `files:{downloads}`, `vscode-recent`, `bookmarks:chrome`.
The top of the file documents all of this.

## Keys

| Key | Does |
|---|---|
| Ctrl+Space | open / close |
| ↑ ↓ · Ctrl+↑ ↓ | move · jump between sections |
| Enter | open / run |
| Ctrl+Enter | show in folder / open file location |
| Ctrl+Shift+Enter | run as administrator |
| Tab | autocomplete (on an app: start its commands) |
| Ctrl+C · Ctrl+Shift+C | copy path or result · copy the file itself |
| Ctrl+1…9 | open the nth result |
| ↓ on an empty box | your most-used items |
| Esc | clear, then close |
| Ctrl+, | settings |

## Settings

`lumen - settings` opens `%APPDATA%\Lumen\settings.json` (it's commented). You can change:
hotkey, theme, accent color, width, preview pane, file and content search, excluded folders, web search
engine, projects folder, the folder for named Office files, and which shell `>` uses.

**About Ctrl+Space:** while Lumen runs it owns Ctrl+Space, so VS Code's *Trigger Suggest* and Excel's
*Select Column* no longer receive it. Either list those apps in `"passThroughApps": ["Code.exe", "EXCEL.EXE"]`
(Ctrl+Space then goes to the app when it's in front), or pick another hotkey such as `"Alt+Space"`.

## Install / build

```powershell
.\install.ps1 -Startup     # copy to %LOCALAPPDATA%\Programs\Lumen, add to Start, start at sign-in
.\install.ps1 -Uninstall
.\build.ps1 -Run           # rebuild from src\ with the C# compiler built into Windows, then restart
```

Tray icon: open, settings, edit commands, rebuild the app index, start with Windows, quit.
Logs: `%LOCALAPPDATA%\Lumen\lumen.log`. Testing without the UI:
`Lumen.exe --headless --dry-run --exec --query "excel - new budget" --out results.txt`.

## Notes

* If Excel, Word or PowerPoint is already open, `- new` adds the new file to that window. If not, Office is
  started through automation, which skips Office add-ins for that session.
* Outlook's `meeting`, `task`, `contact`, `calendar` and `inbox` use classic Outlook's command line. `email` uses your
  default mail app, so it works with new Outlook too.
* File results come from the Windows Search index. To search more places, add them in
  *Settings → Privacy & security → Searching Windows*.

## Source layout

`src/App.cs` (startup, tray, hotkey routing, single instance, headless tests) · `SearchWindow.cs` + `WindowXaml.cs`
(UI) · `SearchEngine.cs` + `ResultFactory.cs` (ranking and grouping) · `Commands.cs` + `Actions.cs` (app commands,
suggestions, Office automation) · `AppIndex.cs`, `FileSearch.cs`, `SystemCatalog.cs`, `Calculator.cs` (providers) ·
`Matcher.cs` (fuzzy match and usage learning) · `defaults/` (the default settings.json and commands.json, built into the exe).
