# HudNudge

HudNudge is a Dalamud plugin that makes the native HUD Layout editor easier to use without replacing vanilla HUD editing. It adds snapping, undo/redo, precise coordinate placement, and pixel placement with arrow buttons or keyboard arrow keys.

## Features

- Keeps the native HUD Layout workflow intact.
- Moves the selected HUD element pixel-by-pixel with on-screen arrows or keyboard arrow keys.
- Supports configurable movement step size.
- Tracks undo and redo for HudNudge moves and native HUD Layout drags.
- Lets you edit the selected element position by anchor point.
- Snaps HUD elements to nearby HUD elements or the screen center.
- Temporarily enables snapping with Shift, or keeps snapping enabled while dragging.
- Includes debug tools for HUD element selection and HUD Layout event logging.

When HUD Layout opens, HudNudge briefly scans the native HUD editor list so undo/redo can reselect elements by name.

## Commands

- `/hudnudge` opens the native HUD Layout editor.
- `/hudnudge save` clicks the native HUD Layout save button.
- `/hudnudge undo` restores the last tracked movement.
- `/hudnudge redo` reapplies the last undone movement.
- `/hudnudge move <dx> <dy>` moves the selected HUD element by a pixel delta. Alias: `delta`.
- `/hudnudge position <x> <y>` places the selected HUD element at absolute top-left screen coordinates. Aliases: `pos`, `set`.
- `/hudnudge debug` opens the debug tools window. Alias: `dev`.
- `/hudnudge logevents` toggles HUD Layout event logging.

## Development

Build the plugin with:

```powershell
dotnet build HudNudge.sln --configuration Release
```

The packaged Release output is written to:

```text
HudNudge\bin\x64\Release\HudNudge\
```

## License

HudNudge is licensed under the GNU General Public License v3.0 or later.

Copyright (C) 2026 Plati

When redistributing or modifying this project, preserve the original copyright notice and attribution.

See [LICENSE](LICENSE) for the full license text.