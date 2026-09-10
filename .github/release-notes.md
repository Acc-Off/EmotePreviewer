## EmotePreviewer

Browse, search and preview FiveM emotes (rpemotes-reborn / scully_emotemenu) on Windows, played back from your own GTA V (Legacy) installation.

- `EmotePreviewer-<version>-win-x64.exe` — self-contained, no .NET runtime needed
- `EmotePreviewer-<version>-win-x64-slim.exe` — needs the .NET 10 Desktop Runtime and ASP.NET Core Runtime (x64)

Before the first start, create the key files once with the [EmotePreviewer Key Tool](https://github.com/Acc-Off/EmotePreviewerKeyTool/releases). Requirements and usage are in the README. No game data, keys or emote resources are included.

### Changes in 0.2.2

- Fixed: the cloth-simulated parts of the story and cutscene peds (the protagonists' jackets and 50 other drawables) were drawn pushed away from the body. Their vertices are stored as barycentric weights over the cloth simulation mesh, not as bone weights; they are now decoded through the `.yld` binding and skinned to the body, so the jacket rests and moves with the ped (without the in-game swing). The **Cloth** toggle is on by default now.
- Fixed: the story characters' faces (`FACIAL_*` rig, e.g. `player_zero`) folded into the skull on clips that carry a `FACIAL_facialRoot` track. Those bones are treated as facial and left to the facial layer, like `FB_*`.
- Ped mesh ETags now hash the served bytes, so a changed extractor never leaves the browser on a stale cached mesh.
- DevTools: `yld` (cloth dictionary dump), `cloth` reports decode problems.
