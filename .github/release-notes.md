## EmotePreviewer

Browse, search and preview FiveM emotes (rpemotes-reborn / scully_emotemenu) on Windows, played back from your own GTA V (Legacy) installation.

- `EmotePreviewer-<version>-win-x64.exe` — self-contained, no .NET runtime needed
- `EmotePreviewer-<version>-win-x64-slim.exe` — needs the .NET 10 Desktop Runtime and ASP.NET Core Runtime (x64)

Before the first start, create the key files once with the [EmotePreviewer Key Tool](https://github.com/Acc-Off/EmotePreviewerKeyTool/releases). Requirements and usage are in the README. No game data, keys or emote resources are included.

### Changes in 0.2.0

- Folder resources are watched: a `.ycd` or Lua file you add or overwrite is picked up a moment later, and the clip on screen is swapped in place without losing the playhead. A **Rescan** button on the settings page does the same on demand (`POST /api/resources/{id}/rescan`).
- Add-on emotes registered through `AnimationListCustom.lua` (`LoadAddonEmotes`) now appear in the catalog.
- DevTools: `skeleton` (dump a skeleton definition as JSON), `yft`, `axes` commands for working on custom animations.
