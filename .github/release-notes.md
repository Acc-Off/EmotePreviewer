## EmotePreviewer

Browse, search and preview FiveM emotes (rpemotes-reborn / scully_emotemenu) on Windows, played back from your own GTA V (Legacy) installation.

- `EmotePreviewer-<version>-win-x64.exe` — self-contained, no .NET runtime needed
- `EmotePreviewer-<version>-win-x64-slim.exe` — needs the .NET 10 Desktop Runtime and ASP.NET Core Runtime (x64)

Before the first start, create the key files once with the [EmotePreviewer Key Tool](https://github.com/Acc-Off/EmotePreviewerKeyTool/releases). Requirements and usage are in the README. No game data, keys or emote resources are included.

### Changes in 0.3.0

- Layering: emotes now play in the game's two animation slots. An emote whose flag makes it an upper-body one (SECONDARY, e.g. flag 51: waving, clapping, holding a flag) plays only its upper body, over the walk style's idle or over the whole-body emote picked above it. **Layer** (next to the search box) opens a second list of upper-body emotes; the upper list then shows whole-body ones, so only pairs the game can play together can be picked. The rules were verified against the game.
- Fixed: list-type clips (about 4,900 of the previewable clips; 3,400 of them point at a later section of a long animation, e.g. `wave_a`) played from the start of their source animation instead of their time window. The baked clip cache is version 4 now, so old caches are rebuilt on first use.
- Fixed: the thigh roll helper bones stayed at the bind pose when a clip carried a track for them (clips written by converters), which bent a raised leg.
- Emotes flagged MOVING (51) count as looping. `GET /api/clipsets/{set}/{clip}` bakes a clip of a movement clip set. DevTools: `ycd` (export a dictionary), `clipinfo [--dump]` (clip structure and raw track values).
