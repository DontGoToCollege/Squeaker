# Squeaker

A mouse cursor customizer for Windows 10 and 11.

- **Appearance**: eight ready-made styles (Classic, Midnight, Graphite, Ocean, Sunset, Mint, Lavender, Rose) or your own fill, gradient, outline and accent colors, at any size from 32 to 256 px. Every pointer (arrow, hand, text, busy, resize and the rest) is restyled together, and you can try them out by hovering before you apply.
- **Custom cursors**: use your own picture (PNG, JPG, BMP, ICO, animated GIF) or cursor file (.cur, .ani) for any pointer, and click the preview to set its hotspot. You can import from a web link, or bring in a whole cursor pack (.zip or install.inf).
- **Click effects**: Ripple, Pulse, Burst, Halo or Focus under the pointer on every click, in any app, with colors, size, duration and an optional squeak.
- **Pointer options**: Windows' own speed, precision, trails, shadow, Ctrl-to-locate, hide-while-typing and snap-to-default settings in one place.

**Restore Windows Cursors** puts back exactly the cursors you had before Squeaker's first Apply.

## Use

Run `Squeaker.exe`. It's a single portable file and needs no installer. Settings are kept in `%AppData%\Squeaker` and applied cursor files in `%LocalAppData%\Squeaker\Applied`.

When click effects are on, closing the window leaves Squeaker running in the tray (right-click the tray icon to quit).

## Build

`.\publish.ps1` self-tests, then writes `Squeaker.exe` and keeps a copy in `Versions\Archive`. Raise `<Version>` in `src\Squeaker\Squeaker.csproj` before each release. `.\publish.ps1 -Test` builds into `dist` without archiving.

`Squeaker.exe --snapshot <folder>` renders every page (light and dark) to PNGs without showing a window.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
