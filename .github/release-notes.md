## EmotePreviewer

Browse, search and preview FiveM emotes (rpemotes-reborn / scully_emotemenu) on Windows, played back from your own GTA V (Legacy) installation.

- `EmotePreviewer-<version>-win-x64.exe` — self-contained, no .NET runtime needed
- `EmotePreviewer-<version>-win-x64-slim.exe` — needs the .NET 10 Desktop Runtime and ASP.NET Core Runtime (x64)

Before the first start, create the key files once with the [EmotePreviewer Key Tool](https://github.com/Acc-Off/EmotePreviewerKeyTool/releases). Requirements and usage are in the README. No game data, keys or emote resources are included.

### Changes in 0.2.1

- Fixed: peds whose component drawables embed their own (partial) skeleton — `mp_f_deadhooker`, the three protagonists (`player_zero` / `player_one` / `player_two`), `cs_wade`, `ig_wade`, `cs_stretch`, `ig_tracydisanto` — looked right at rest but fell apart as soon as an emote played. Their skin bone indices are now mapped to the ped skeleton by bone tag, the way the game does it.
- New **Cloth** toggle in the viewer (off by default): parts the game shapes with its cloth simulation (the protagonists' and cutscene peds' jackets) only exist in the files as a spread-out starting shape, so they are left out unless you turn them on. The button is enabled only for peds that have such parts.
- DevTools: `skinbones` and `cloth` scans, `geom --bones`.
