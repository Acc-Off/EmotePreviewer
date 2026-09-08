# 開発ガイド

[English](development.md) | 日本語

EmotePreviewer のビルド・構成・テストについて。アプリが何をするか、使い方は [README](../README.ja.md) を参照。設計は [app-design.ja.md](app-design.ja.md)、実装計画は [app-plan.ja.md](app-plan.ja.md)、`.ycd` フォーマットのメモは [spec/ycd-format.ja.md](spec/ycd-format.ja.md)。

## ビルド

.NET 10 SDK と Node.js 22 以上が必要。`dotnet build` がフロントエンドの `npm ci` と `npm run build` を実行し、`dist/` を exe に埋め込む。

```
dotnet build EmotePreviewer.slnx -c Release
dotnet test  EmotePreviewer.slnx -c Release --no-build
dotnet run   -c Release --no-build --project src/EmotePreviewer.App -- --no-browser --data-dir <一時フォルダ>
```

- Node 無しで C# だけをビルドするときは `-p:SkipWebBuild=true`
- フロントエンドの開発は `src/EmotePreviewer.Web` で `npm run dev`（Vite、`/api` を `127.0.0.1:20300` にプロキシ）。環境変数 `EMOTEPREVIEWER_WEBROOT` に `dist` のパスを指定すると、exe を再ビルドせずに配信内容を差し替えられる
- 配布用: `dotnet publish src/EmotePreviewer.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`。リリースは `Directory.Build.props` の `<Version>` と一致する `v*` タグを push したときに [.github/workflows/release.yml](../.github/workflows/release.yml) がビルドする
- **動作確認は必ず `--data-dir` に一時フォルダを指定する**。本物の `%LOCALAPPDATA%\EmotePreviewer` を触らない

## コマンドラインとデータフォルダ

```
EmotePreviewer.exe [--port 20300] [--data-dir <dir>] [--gta <dir>] [--keys <dir>]
                   [--no-browser] [--app] [--verbose] [--help]
```

| 引数 | 意味 |
|---|---|
| `--port` | 待ち受けポート。既定 20300。使用中なら +1 ずつ 10 回まで試す |
| `--data-dir` | 設定・キャッシュ・ログの置き場。既定 `%LOCALAPPDATA%\EmotePreviewer` |
| `--gta` / `--keys` | 設定ファイルより優先する一時的な上書き |
| `--no-browser` | 起動時にブラウザを開かない |
| `--app` | Edge / Chrome の `--app=` でタブやアドレスバーの無いウィンドウとして開く |
| `--verbose` | Debug ログ |

2 つ目の exe を起動すると、既に動いているインスタンスの URL をブラウザで開いて終了する。鍵ファイルは `--keys`、環境変数 `EMOTEPREVIEWER_KEYS`、設定の鍵フォルダ、データフォルダ内の `keys\` の順に探す。

```
<data-dir>/
  settings.json     設定（UI から保存される）
  instance.json     起動中の URL と PID
  keys/             鍵ファイルの既定の置き場
  resources/<id>/   GitHub から取得したリソース
  cache/            抽出済みスケルトンなど（消しても再生成される）
  logs/             ローリングログ
```

## 構成

| パス | 内容 |
|---|---|
| `src/EmotePreviewer.Core` | Lua 解析（`Catalog/`）、データ契約（`Model/`）、姿勢計算と焼き込み（`Anim/`）、`.ycd` デコーダとクリップセット表（`Rage/`）、GTA 検出（`Gta/`）、gta-toolkit アダプタ（`Adapters/GtaToolkit/`）、テクスチャ（`Textures/`） |
| `src/EmotePreviewer.App` | コンソール exe。Kestrel + Minimal API + SSE、埋め込み SPA、設定・状態・クリップ配信 |
| `src/EmotePreviewer.Web` | Vite + React + TypeScript + Zustand の SPA。three.js で描画 |
| `tools/EmotePreviewer.DevTools` | 開発者向けコンソール（後述） |
| `tests/EmotePreviewer.Core.Tests` | xunit。ClipBaker のレイアウト、複数ソースのカタログ、クリップセット解決、回帰フィクスチャによるデコーダ検証、GTA 実機に対する検証（無ければスキップ） |
| `tests/EmotePreviewer.App.Tests` | API の統合テスト（実際の Kestrel を空きポートで起動。GTA 不要） |
| `tests/EmotePreviewer.Fixtures` | 回帰フィクスチャの型と生成器 |
| `third_party/gta-toolkit` | carmineos/gta-toolkit（MIT）の `RageLib` / `RageLib.GTA5` |
| `Docs/` | 設計・計画・フォーマットメモとこのガイド |

### プレビューができるまで

1. 各リソースの Lua データ（rpemotes-reborn の `AnimationList.lua`、scully の `shared/data/*.lua`）からカタログを約 0.1 秒で組む。エントリには安定した id `source/category/command` が付く。
2. ゲームのアーカイブを索引する（約 1.5 秒）。`.ycd` / `.yft` / `.ydr` / `.ydd` / `.ytd` を名前ハッシュで、`update.rpf` と DLC パックは `dlclist.xml` の順に、後のものが前のものを上書きする。
3. 索引が済んだら各エントリの辞書とクリップの有無を確認する。歩行スタイルは `clip_sets.ymt`（自身の辞書、次にフォールバック先）で解決する。結果はカタログ DTO の `previewable` と `previewReason` になる。
4. エントリを選ぶとクリップをデコードし、設定中の ped のスケルトンに対してネイティブ fps（上限 60）でローカル変換に焼き込み、float32 ブロックとして配信する。ブラウザは three.js のボーン階層を組み、`AnimationMixer` でブロックを再生し、ルートモーションをクライアント側で合成し、小道具と相手 ped をボーンに取り付ける。

### API の概要

すべて `/api/` 配下。`GET /api/status`（状態）、`GET /api/events`（SSE: `status` / `catalog` / `resource`）、`GET /api/catalog`（共有エモートは `partner` に相手の id・クリップ・主から見た配置を含む）、`GET/PUT /api/settings`、`GET /api/diagnostics`、`/api/resources`（GET / POST / DELETE / `refresh` は GitHub リソースの再ダウンロード / `rescan` はリソースのファイルの読み直し: カタログを再構築し、そのフォルダ配下から読んだ辞書・焼き込み済みクリップ・メッシュのキャッシュを捨てる。フォルダリソースは `FileSystemWatcher` で `.ycd` / `.ydr` / `.lua` の変更を監視し、最後の変更から 1.5 秒後に自動で再スキャンする。設定の `watchFolders` で止められる）、`GET /api/skeleton?ped=`、`GET /api/emotes/{id}/clip` と `clip.bin`（焼き込み済みのローカル変換、float32 の `[frame][bone] × (px,py,pz,qx,qy,qz,qw)`、ルートモーションは別ブロック。`?ped=` で焼き込み先のスケルトンを選ぶ）、`GET /api/dictionaries/{name}/clips`、`GET /api/clips/{dict}/{clip}.bin`、`GET /api/props/{model}.bin`（小道具メッシュ）、`GET /api/peds` と `GET /api/peds/{ped}`（ped 一覧と既定部位）、`GET /api/ped/{ped}/{component}.bin`（ped のスキンメッシュ）、`GET /api/textures/{prop|ped}/{name}/{texture}.dds`（ディフューズテクスチャ）。詳細は [app-design.ja.md](app-design.ja.md)。

## DevTools

回帰フィクスチャの生成やカバレッジ確認など、配布物に含めないコマンド。リポジトリの `data/` にリソースフォルダを置く（`data/` はバージョン管理外）。

```
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- regression   # tests/fixtures/ にフィクスチャを生成
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- coverage     # 辞書＋クリップが実在する件数
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- bake         # 全件を焼き込んで例外を数える
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- find <名前>  # 辞書／yft がどのアーカイブにあるか
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- skeleton <yft名> [out.json]  # スケルトン定義（親子・タグ・ローカル変換）を書き出す
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- yft <yft名> [out.yft]  # GTAアーカイブから .yft を書き出す
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- pose <dict> <clip> [t]   # 時刻 t のボーン座標
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- mesh <model...>  # 小道具メッシュの抽出統計
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- props [--scan] [--textures]  # カタログが参照する小道具モデル（とテクスチャ）の解決率
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- ped <folder> <file...>   # ped コンポーネントの抽出（例: mp_m_freemode_01 uppr_000_r）
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- walks [--list]   # 歩行クリップセットの解決状況（clip_sets.ymt 経由／フォールバック／未解決）
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- clipsets [<set> [clip]]  # clip_sets.ymt の所在と、1 セットのフォールバック連鎖・解決先
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- tex <model | folder/file...>  # ドロワブルのシェーダが参照するテクスチャ（埋め込み／外部）
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- ytd <name | folder/name>     # テクスチャ辞書の中身
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- ydd <name>   # ドロワブル辞書の中身（単体型 ped の部位一覧）
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- pedtex <folder>  # ped フォルダのテクスチャ辞書一覧
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- missing      # 見つからない小道具・辞書・クリップの内訳
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- peds [filter] # ゲームデータ内の ped 一覧（格納形式・分類）
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- pedinfo <ped> # ped の部位ごとの既定ドロワブルとテクスチャの解決
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- dds <model|ped> <texture> [out.dds]  # テクスチャを DDS に書き出す
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- animals      # 動物エモートがどの ped に割り当たるか
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- shared [--gta-check]  # 共有エモートの相手・配置・クリップの解決状況
```

`pose` は `--skel <yft名>` で別のスケルトン（例: `a_c_rottweiler`）に対して評価できる。`--data <folder>` / `--gta <folder>` / `--keys <folder>` で場所を上書きできる。

## テスト

デコーダの回帰テストは、自分の GTA V から生成したフィクスチャに対して回す。フィクスチャはゲームデータを含むためリポジトリには入れない（無ければスキップされる）。GitHub Actions ではビルドと GTA 不要のテストだけを実行する。

```
dotnet run -c Release --no-build --project tools/EmotePreviewer.DevTools -- regression
dotnet test -c Release --no-build
```

## 約束事

- 識別子・コメント・ログ・コミットメッセージは英語。UI 文字列は `src/EmotePreviewer.Web/src/shared/locales` に集約する（日本語がキーの正、英語はそれに追従）。コードに言語リテラルを置かない。
- 公開 API とディスク上の形式（設定、キャッシュ）は [app-design.ja.md](app-design.ja.md) に書いてある。コードと一緒に更新する。
- ゲームデータ・鍵ファイル・エモートリソースはコミットしない。`data/`、`tests/fixtures/`、`*.dat` は無視される。
