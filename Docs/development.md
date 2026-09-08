# Development guide

English | [日本語](development.ja.md)

How EmotePreviewer is built, laid out and tested. For what the app does and how to use it, see the [README](../README.md). The design documents ([app-design.ja.md](app-design.ja.md), [app-plan.ja.md](app-plan.ja.md)) and the [`.ycd` format notes](spec/ycd-format.ja.md) are in Japanese.

## Building

Requires the .NET 10 SDK and Node.js 22 or later. `dotnet build` runs `npm ci` and `npm run build` for the frontend and embeds `dist/` into the exe.

```
dotnet build EmotePreviewer.slnx -c Release
dotnet test  EmotePreviewer.slnx -c Release --no-build
dotnet run   -c Release --no-build --project src/EmotePreviewer.App -- --no-browser --data-dir <temp folder>
```

- To build only the C# side without Node, pass `-p:SkipWebBuild=true`
- Frontend development: `npm run dev` in `src/EmotePreviewer.Web` (Vite, proxies `/api` to `127.0.0.1:20300`). Setting the `EMOTEPREVIEWER_WEBROOT` environment variable to a `dist` path serves that folder instead of the embedded one, without rebuilding the exe
- Distribution: `dotnet publish src/EmotePreviewer.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`. Releases are built by [.github/workflows/release.yml](../.github/workflows/release.yml) when a `v*` tag matching `<Version>` in `Directory.Build.props` is pushed
- **Always pass a temporary folder as `--data-dir` when testing.** Do not touch the real `%LOCALAPPDATA%\EmotePreviewer`

## Command line and data folder

```
EmotePreviewer.exe [--port 20300] [--data-dir <dir>] [--gta <dir>] [--keys <dir>]
                   [--no-browser] [--app] [--verbose] [--help]
```

| Argument | Meaning |
|---|---|
| `--port` | Listening port, default 20300. When busy, +1 is tried up to 10 times |
| `--data-dir` | Where settings, caches and logs live. Default `%LOCALAPPDATA%\EmotePreviewer` |
| `--gta` / `--keys` | Temporary overrides that take precedence over the settings file |
| `--no-browser` | Do not open the browser on start |
| `--app` | Open with Edge / Chrome `--app=`, a window without tabs or address bar |
| `--verbose` | Debug logging |

Starting a second exe opens the URL of the running instance in the browser and exits. Key files are searched in this order: `--keys`, the `EMOTEPREVIEWER_KEYS` environment variable, the key folder in the settings, `keys\` inside the data folder.

```
<data-dir>/
  settings.json     settings (saved from the UI)
  instance.json     URL and PID of the running instance
  keys/             default place for the key files
  resources/<id>/   resources fetched from GitHub
  cache/            extracted skeletons etc. (safe to delete; regenerated)
  logs/             rolling logs
```

## Layout

| Path | Contents |
|---|---|
| `src/EmotePreviewer.Core` | Lua parsing (`Catalog/`), data contracts (`Model/`), pose evaluation and baking (`Anim/`), `.ycd` decoder and clip set table (`Rage/`), GTA detection (`Gta/`), gta-toolkit adapter (`Adapters/GtaToolkit/`), textures (`Textures/`) |
| `src/EmotePreviewer.App` | Console exe. Kestrel + Minimal API + SSE, embedded SPA, settings, state and clip serving |
| `src/EmotePreviewer.Web` | Vite + React + TypeScript + Zustand SPA. three.js renders the figure |
| `tools/EmotePreviewer.DevTools` | Developer console (see below) |
| `tests/EmotePreviewer.Core.Tests` | xunit. ClipBaker layout, multi-source catalog, clip set resolution, decoder verification against regression fixtures, checks against a real GTA install (skipped when absent) |
| `tests/EmotePreviewer.App.Tests` | API integration tests (real Kestrel on a free port; no GTA needed) |
| `tests/EmotePreviewer.Fixtures` | Regression fixture types and generator |
| `third_party/gta-toolkit` | `RageLib` / `RageLib.GTA5` from carmineos/gta-toolkit (MIT) |
| `Docs/` | Design, plan, format notes and this guide |

### How a preview is produced

1. The catalog is built from the Lua data files of each resource (rpemotes-reborn `AnimationList.lua`, scully `shared/data/*.lua`) in about 0.1 s. Every entry gets a stable id `source/category/command`.
2. The game archives are indexed (about 1.5 s): every `.ycd`, `.yft`, `.ydr`, `.ydd` and `.ytd` by name hash, `update.rpf` and the DLC packs in `dlclist.xml` order, later archives overriding earlier ones.
3. Once indexed, each entry is checked for a dictionary and a clip. Walk styles are resolved through `clip_sets.ymt` (own dictionary, then fallback sets). The result is `previewable` + `previewReason` in the catalog DTO.
4. When an entry is selected the clip is decoded and baked to local bone transforms at its native frame rate (capped at 60 fps) for the configured ped's skeleton, and served as a float32 block. The browser builds a three.js bone hierarchy, plays the block with an `AnimationMixer`, composes root motion client-side, and attaches props and the partner ped by bone.

### API overview

Everything lives under `/api/`. `GET /api/status` (state), `GET /api/events` (SSE: `status` / `catalog` / `resource`), `GET /api/catalog` (shared emotes carry the partner's id, clip and placement as seen from the main ped in `partner`), `GET/PUT /api/settings`, `GET /api/diagnostics`, `/api/resources` (GET / POST / DELETE / `refresh` to download a GitHub resource again / `rescan` to re-read a resource's files: the catalog is rebuilt and the dictionaries, baked clips and meshes read from under its folder are dropped; folder resources are also rescanned automatically by a `FileSystemWatcher` when `.ycd` / `.ydr` / `.lua` files change, 1.5 s after the last change, unless `watchFolders` is off in the settings), `GET /api/skeleton?ped=`, `GET /api/emotes/{id}/clip` and `clip.bin` (baked local transforms, float32 `[frame][bone] × (px,py,pz,qx,qy,qz,qw)`, root motion in a separate block; `?ped=` selects the skeleton to bake for), `GET /api/dictionaries/{name}/clips`, `GET /api/clips/{dict}/{clip}.bin`, `GET /api/props/{model}.bin` (prop mesh), `GET /api/peds` and `GET /api/peds/{ped}` (ped list and default components), `GET /api/ped/{ped}/{component}.bin` (skinned ped mesh), `GET /api/textures/{prop|ped}/{name}/{texture}.dds` (diffuse textures). Details in [app-design.ja.md](app-design.ja.md).

## DevTools

Commands that are not part of the distribution, such as generating regression fixtures and checking coverage. Put the resource folders under `data/` in the repository (`data/` is not versioned).

```
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- regression   # generate the fixtures under tests/fixtures/
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- coverage     # how many entries have a real dictionary + clip
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- bake         # bake everything and count exceptions
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- find <name>  # which archive a dictionary / yft is in
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- skeleton <yft name> [out.json]  # dump a skeleton definition (hierarchy, tags, local transforms)
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- yft <yft name> [out.yft]  # extract a .yft from GTA archives
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- pose <dict> <clip> [t]   # bone positions at time t
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- mesh <model...>  # prop mesh extraction statistics
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- props [--scan] [--textures]  # how many prop models (and textures) referenced by the catalog resolve
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- ped <folder> <file...>   # extract ped components (e.g. mp_m_freemode_01 uppr_000_r)
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- walks [--list]   # how the walk clip sets resolve (clip_sets.ymt / fallback / unresolved)
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- clipsets [<set> [clip]]  # where clip_sets.ymt lives; one set's fallback chain and resolved clip
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- tex <model | folder/file...>  # textures a drawable's shaders reference (embedded / external)
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- ytd <name | folder/name>     # contents of a texture dictionary
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- ydd <name>   # contents of a drawable dictionary (component list of a single-file ped)
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- pedtex <folder>  # texture dictionaries of a ped folder
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- missing      # breakdown of missing props, dictionaries and clips
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- peds [filter] # ped models in the game data (storage form, category)
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- pedinfo <ped> # default drawable and texture per component slot of a ped
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- dds <model|ped> <texture> [out.dds]  # write a texture as DDS
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- animals      # which ped each animal emote maps to
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- shared [--gta-check]  # partner, placement and clip status of the shared emotes
```

`pose` accepts `--skel <yft name>` to evaluate against another skeleton (e.g. `a_c_rottweiler`). `--data <folder>` / `--gta <folder>` / `--keys <folder>` override the locations.

## Tests

The decoder regression tests run against fixtures generated from your own GTA V. The fixtures contain game data and are not committed (the tests are skipped when they are missing). GitHub Actions builds and runs only the tests that do not need GTA.

```
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- regression
dotnet test -c Release --no-build
```

## Conventions

- Identifiers, comments, logs and commit messages are in English. UI strings live in `src/EmotePreviewer.Web/src/shared/locales` (Japanese is the canonical key set, English mirrors it); no language literals in code.
- The public API and the on-disk formats (settings, cache) are described in [app-design.ja.md](app-design.ja.md); update it together with the code.
- Game data, key files and emote resources are never committed. `data/`, `tests/fixtures/` and `*.dat` are ignored.
