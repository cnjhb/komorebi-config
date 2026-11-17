# komorebi-taskbar-info (Windhawk mod)

A Windhawk mod that shows the komorebi workspace line **inside the Windows 11
taskbar clock area**, refreshed every second.

It is a modified copy of [Taskbar Clock
Customization](https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-clock-customization.wh.cpp)
by [m417z](https://github.com/m417z), which is published under **GPL-3.0** (see
`LICENSE`). The modifications are:

- `@id` / `@name` / `@version` / author / homepage changed
- the web-content update interval is interpreted in **seconds** instead of
  minutes, so the local endpoint can be polled at 1 Hz

Everything else (the taskbar clock hook, styling, patterns) comes from upstream.

## Data source

`WorkspaceTray` serves a plain-text line on `http://127.0.0.1:17321/`:

```
[1] 2· 3 4 5 6 7 8 9  ·  BSP
```

- `[n]` = focused workspace
- `n·` = workspace has windows
- `BSP` = layout of the focused workspace

`/full` additionally appends the focused window title. The mod uses `/` for the
bottom line and `/full` for the clock tooltip.

The line describes the **primary monitor** (index 0), because the taskbar clock
only exists on the primary taskbar.

## Requirements

- Windhawk 1.7.x installed (it ships the clang toolchain used below)
- `WorkspaceTray` installed and running (see `../README.md`)

## Build

```powershell
cd windhawk
.\build.ps1
```

Downloads the current upstream source, applies the patches, and compiles
`komorebi-taskbar-info.dll` with Windhawk's bundled clang.

## Install / uninstall

```powershell
# self-elevates (writes HKLM + copies the DLL into Windhawk's engine folder)
.\install.ps1 -RestartExplorer

# remove
.\install.ps1 -Uninstall
```

Installing without `-RestartExplorer` takes effect the next time `explorer.exe`
starts; restarting it makes the mod load immediately (the taskbar blinks once).

## Settings

The installer writes these under
`HKLM\SOFTWARE\Windhawk\Engine\Mods\komorebi-taskbar-info\Settings`:

| Setting | Value | Meaning |
|---|---|---|
| `TopLine` | `%date%  %time%` | clock top line (date + time) |
| `BottomLine` | `%web1%` | workspace line |
| `TooltipLine` | `%web2_full%` | hover text (with window title) |
| `MaxWidth` | `340` | max clock width (Win11) |
| `DateStyle.FontSize` / `TimeStyle.FontSize` | `14` | physical pixels |
| `WebContentsUpdateInterval` | `1` | seconds (patched behaviour) |
| `WebContentsItems[0].Url` | `http://127.0.0.1:17321/` | compact line |
| `WebContentsItems[1].Url` | `http://127.0.0.1:17321/full` | line with title |

Windhawk's own "Mod settings" UI can also edit these once the mod is installed.

## Notes / limitations

- The clock only exists on the primary taskbar, so this shows the primary
  monitor's workspaces. The per-monitor capsules from `WorkspaceTray` still
  cover every screen.
- Windhawk injects into `explorer.exe`; a Windows update can break the hook
  until upstream adapts. `install.ps1 -Uninstall` removes the mod cleanly.
- The mod polls `127.0.0.1` only; no external network access.
