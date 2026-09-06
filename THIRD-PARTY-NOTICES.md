# Third-Party Notices

EmotePreviewer is distributed under the MIT License. It bundles or uses the third-party works listed below. This file is embedded in the executable and shown on the settings page.

## Backend (.NET)

| Component | License | URL | Notes |
|---|---|---|---|
| .NET runtime / ASP.NET Core | MIT | https://github.com/dotnet/runtime | Kestrel serves the UI on 127.0.0.1 |
| gta-toolkit (`RageLib`, `RageLib.GTA5`) by carmineos | MIT | https://github.com/carmineos/gta-toolkit | Vendored in `third_party/gta-toolkit` (RPF archives, RSC7 resources, skeletons). See its `LICENSE.md` |
| NLua | MIT | https://github.com/NLua/NLua | Evaluates the Lua data files of emote resources |
| KeraLua / Lua 5.4 (via NLua) | MIT | https://github.com/NLua/KeraLua, https://www.lua.org/license.html | |

## Frontend (npm, bundled into the executable)

| Package | License | URL |
|---|---|---|
| three.js | MIT | https://github.com/mrdoob/three.js |
| React / React DOM | MIT | https://github.com/facebook/react |
| Zustand | MIT | https://github.com/pmndrs/zustand |

Build tools (Vite, TypeScript, esbuild, Rollup) are not part of the distributed application.

## Not included

- **GTA V game data** and the **RPF decryption keys** are never bundled. The application reads the user's own installation; the key files are created by the user with the separate EmotePreviewerKeyTool.
- **Emote resources** (rpemotes-reborn, scully_emotemenu and others) belong to their respective authors and are read from folders the user provides. See each resource's license before redistributing anything derived from them.
