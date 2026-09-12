## EmotePreviewer

Browse, search and preview FiveM emotes (rpemotes-reborn / scully_emotemenu, and the older rpemotes / dpemotes) on Windows, played back from your own GTA V (Legacy) installation.

- `EmotePreviewer-<version>-win-x64.exe` — self-contained, no .NET runtime needed
- `EmotePreviewer-<version>-win-x64-slim.exe` — needs the .NET 10 Desktop Runtime and ASP.NET Core Runtime (x64)

Before the first start, create the key files once with the [EmotePreviewer Key Tool](https://github.com/Acc-Off/EmotePreviewerKeyTool/releases). Requirements and usage are in the README. No game data, keys or emote resources are included.

### Changes in 0.4.0

- Resources: the older menus load too — [rpemotes](https://github.com/Daudeuf/rpemotes) (the version before rpemotes-reborn) and [dpemotes](https://github.com/andristum/dpemotes). Both are offered for download on the settings page, and a copy on disk works as a folder. `types.lua` is no longer required, dpemotes' `DP` tables and `Client/` folder are recognised, and lists that read `Config` (the `PtfxInfo` lines) no longer fail to load. Their content mostly overlaps rpemotes-reborn; the point is checking a server that still runs one of them.
- Fixed: props stored as breakable fragments (the food trays, the bin, the road sign, the electric guitar — 10 of the 500 prop models) drew their parts at the wrong place because the bone each part sits on was ignored: the cup sank through the tray. Present since 0.1.0.
- Viewer: a frame is drawn only when something changed (playback, camera, a load or a toggle), and the loop stops while the "EmotePreviewer has exited" overlay is shown. Before, the blurred overlay over a canvas redrawn 60 times a second kept the GPU busy and could make video playback in other windows stutter while the tab was visible.
- DevTools: `geom` reads fragments as well as drawables; `--bones` prints each bone's offset.
