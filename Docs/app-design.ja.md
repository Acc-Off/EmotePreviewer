# EmotePreviewer アプリ設計

基盤検証（コンソール）で技術的な成立を確認したので、その成果を土台にした実アプリの設計をまとめる。実装の順序は [app-plan.ja.md](app-plan.ja.md) を参照。

構成は [FxDeck](https://github.com/Acc-Off/FxDeck) に揃える: **単一の exe がローカル HTTP サーバーを立て、UI は普通のブラウザで開く。** ログはコンソールに流れる。

## 0. 前提と目標

### 確認済みの事実

| 項目 | 結果 |
|---|---|
| Lua 定義の解析（rpemotes-reborn + scully_emotemenu） | 6,532 件、0.1 秒 |
| GTA V アーカイブの索引（ycd / yft） | 約 1.5 秒（毎回スキャン） |
| Animation 種別のうち辞書＋クリップが実在するもの | 5,896 / 6,074 件（97.1%） |
| スケルトン | `mp_m_freemode_01.yft`、128 ボーン。女性エモートも同じボーン構成なので代用可 |
| 描画 | ボーン位置を JSON に書き出し、three.js の棒人間ビューアで再生できた |

### 目標（v1）

- エモートを **一覧・検索** し、選んだものを **その場でプレビュー** できる Windows ツール
- プレビューは棒人間（ボーン）で十分。小道具とキャラクターのメッシュは段階的に追加する
- 起動から一覧表示まで 1 秒以内。クリップ選択から再生開始まで 0.5 秒以内

### 非目標（v1 では扱わない）

- GTA V Enhanced 版
- 表情（Expressions）の再生。顔ボーンの扱いが別物になる
- 動物 ped のスケルトン。人型スケルトンでの再生は崩れるので「対象外」と表示する
- ゲームと同等の見た目（テクスチャ、シェーダ、衣装）
- LAN や外部からのアクセス。`127.0.0.1` 専用

## 1. 全体構成

```
┌─ EmotePreviewer.exe（コンソール, net10.0-windows）──────────────────┐
│  Program      : 引数解析、単一インスタンス、ブラウザ起動、Ctrl+C     │
│  Web/         : Kestrel（127.0.0.1:20300）, REST API, SSE, 埋め込み SPA │
│  Services/    : AppState（GTA 初期化・索引・キャッシュ）, Settings,   │
│                 ResourceManager（取得・更新）, ClipService, MeshService │
│  Logging/     : コンソール + ファイル                                   │
└──────────┬───────────────────────────────────────────────────────────┘
           │ 参照                                    ▲ HTTP / SSE
┌──────────▼──────────────────────────────┐   ┌──────┴──────────────────────┐
│ EmotePreviewer.Core (net10.0)           │   │ ブラウザ（Edge / Chrome）    │
│  Catalog / Model / Anim / Rage /        │   │  EmotePreviewer.Web の SPA   │
│  Adapters(gta-toolkit) / Resources      │   │  React + three.js            │
└─────────────────────────────────────────┘   └─────────────────────────────┘
```

| プロジェクト | 役割 | 現状 |
|---|---|---|
| `src/EmotePreviewer.Core` | 解析・デコード・姿勢計算。UI と HTTP に依存しない | 継続 |
| `src/EmotePreviewer.App` | コンソール exe。Kestrel + Minimal API + SPA 埋め込み | **新規** |
| `src/EmotePreviewer.Web` | Vite + React + TypeScript の SPA。`dist/` を exe に埋め込む | **新規** |
| `tools/EmotePreviewer.DevTools` | 回帰フィクスチャ生成、カバレッジ診断のコンソール | 検証用コンソールから必要分だけ移す |
| `tests/EmotePreviewer.Core.Tests` | xunit | 継続。ClipBaker / 索引のテストを追加 |
| `tests/EmotePreviewer.App.Tests` | API の統合テスト（空きポートで実際の Kestrel を起動） | **新規** |
| `src/EmotePreviewer.Viewer`, `tools/bundle_viewer.py` | 検証用のスタンドアロン HTML | `Web` に移植して削除済み |

ブラウザ方式を選んだ理由:

- ホスト側は Minimal API のエンドポイントだけになり、WebView2 の初期化・仮想ホスト・独自ブリッジが要らない
- フレームデータや頂点データは `application/octet-stream` を `fetch` するだけ。localhost なら数 MB でも一瞬で、URL 単位でブラウザキャッシュが効く
- Vite の dev サーバーと DevTools でそのまま開発・デバッグできる。API は curl と統合テストで検証できる
- 配布・ビルドの仕組み（`BuildWeb` / `EmbedWeb` ターゲット、`EmbeddedWebRoot`、単一 exe）を FxDeck から流用できる

## 2. ホスト（EmotePreviewer.App）

### コマンドライン

```
EmotePreviewer.exe [--port 20300] [--data-dir <dir>] [--gta <dir>] [--keys <dir>]
                   [--no-browser] [--app] [--verbose] [--help]
```

| 引数 | 意味 |
|---|---|
| `--port` | 待ち受けポート。既定 20300。使用中なら +1 ずつ 10 回まで試す |
| `--data-dir` | 設定・キャッシュ・取得リソースの置き場。既定 `%LOCALAPPDATA%\EmotePreviewer`。**検証時は必ず一時フォルダを指定する** |
| `--gta` / `--keys` | 設定ファイルより優先する一時的な上書き |
| `--no-browser` | 起動時にブラウザを開かない |
| `--app` | `msedge --app=<url>`（無ければ `chrome`）で、タブやアドレスバーの無いウィンドウとして開く |
| `--verbose` | Debug ログ |

### 起動シーケンス

1. 引数と `settings.json` を読む
2. 単一インスタンス（名前付き Mutex）。既に動いていれば、そのインスタンスの URL をブラウザで開いて終了する。URL は `data-dir/instance.json` から読む
3. Kestrel を `127.0.0.1` のみに bind。`Host` ヘッダが `127.0.0.1` / `localhost` 以外なら 400
4. カタログを同期で構築する（0.1 秒）。リソース未設定なら空のまま進む
5. ブラウザを開く（`--no-browser` でなければ）
6. **バックグラウンドで GTA を初期化**（鍵、索引、スケルトン）。進捗は SSE の `status` で流す。完了までは一覧と検索だけ使え、プレビューは「準備中」
7. Ctrl+C、コンソールを閉じる、または UI の「終了」で停止。`instance.json` を消す

索引は 1.1〜1.4 秒、起動から準備完了まで約 1.8 秒なので索引キャッシュは入れない（§8）。

### 設定

`data-dir/settings.json`:

```json
{
  "gtaFolder": null,
  "keysFolder": null,
  "resources": [
    { "id": "rpemotes-reborn", "path": "C:\\...\\rpemotes-reborn", "origin": "folder" },
    { "id": "scully_emotemenu", "path": "<data-dir>\\resources\\scully_emotemenu", "origin": "github", "ref": "main", "enabled": true }
  ],
  "ped": "mp_m_freemode_01",
  "partnerPed": null,
  "viewer": { "showHelperBones": false, "rootMotion": false, "showProps": true, "showMesh": false, "showTextures": true, "showCloth": true, "animalPeds": true, "showPartner": true, "theme": "system", "language": "auto" }
}
```

`null` はレジストリ検出／既定フォルダの意味。鍵フォルダの解決順は `--keys` → 環境変数 `EMOTEPREVIEWER_KEYS` → `data-dir/keys`。設定は UI の変更で即保存し、ファイルを直接編集した場合は再起動で反映（ホットリロードはしない）。

### データ配置

```
<data-dir>/
  settings.json
  instance.json         起動中の URL と PID（単一インスタンス用）
  keys/                 鍵ファイル 4 つ（ユーザーが KeyTool で生成）
  resources/<id>/       GitHub から取得したリソース（origin=github のもの）
  cache/                索引キャッシュ、抽出済みスケルトン等（消しても再生成される）
  logs/                 直近数回分のログ
```

### ログ

`ILogger` をコンソール（stdout）とファイル（`logs/`、ローリング）の両方に出す。起動時に URL、GTA フォルダ、鍵の有無、索引件数を必ず表示する。クリップのロード失敗は 1 行の Warning にし、例外のスタックは Debug に落とす。UI のログドロワー（任意）は SSE の `log` イベントで流す。

## 3. HTTP API

すべて `/api/` 配下、JSON は camelCase。エラーは `{ "error": { "code": "CLIP_NOT_FOUND", "message": "..." } }` で、コードは UI の表示分岐に使う。

### 状態・設定

| メソッド | パス | 内容 |
|---|---|---|
| GET | `/api/status` | `gta: missingGta / missingKeys / indexing / ready / error`、進捗、索引件数、バージョン |
| GET | `/api/events` | **SSE**。`status`（GTA 初期化）、`catalog`（カタログ再構築）、`resource`（取得進捗）、`log` |
| GET / PUT | `/api/settings` | 設定の読み書き。PUT は検証して保存し、GTA や鍵が変わったら初期化をやり直す。`viewer` には `showHelperBones` / `rootMotion` / `showProps` / `showMesh` / `showTextures` / `showCloth` / `animalPeds` / `showPartner` / `theme` / `language`。`partnerPed` は共有エモートの 2 体目の ped（null = `ped` と同じ。`ped` と同じ名前は null に正規化）。`ped` の変更は再索引せず `status` を送り直すだけ |
| GET | `/api/notices` | 埋め込みの `THIRD-PARTY-NOTICES.md` |
| GET | `/api/diagnostics` | GTA パス、鍵ファイルの有無、索引件数、リソース一覧、バージョン、ログのパス |
| POST | `/api/dialogs/folder` | サーバー側で `FolderBrowserDialog` を出し、選ばれたパスを返す（STA スレッド）。ブラウザからは実パスが取れないため |
| POST | `/api/quit` | 終了 |

### カタログ

| メソッド | パス | 内容 |
|---|---|---|
| GET | `/api/catalog` | 全エントリの DTO（§4.1）と `revision` / `previewResolved`。検索・絞り込みはブラウザ側 |
| GET | `/api/dictionaries/{name}/clips` | 辞書内のクリップ一覧（クリップ名が一致しないエントリの手動選択用） |

### プレビュー

| メソッド | パス | 内容 |
|---|---|---|
| GET | `/api/skeleton?ped=` | バインドポーズ。ボーン名・タグ・親・ローカル変換。`ped` 省略時は設定中の ped |
| GET | `/api/emotes/{id}/clip?ped=` | 焼き込み結果のメタデータ（fps, frames, duration, boneCount, hasRootMotion, skeleton, warnings）。`ped` 省略時は動物エモートなら対応する動物 ped、それ以外は設定中の ped |
| GET | `/api/emotes/{id}/clip.bin?ped=` | 焼き込み結果の本体（`application/octet-stream`、§4.2 のレイアウト） |
| GET | `/api/clips/{dict}/{clip}.bin?ped=` | 辞書・クリップ直接指定版（手動選択用）。メタは `/clip` 相当 |
| GET | `/api/clipsets/{set}/{clip}` / `.bin?ped=` | 移動クリップセット（`move_m@generic` など）のクリップを `clip_sets.ymt` で辞書に解決して焼く。ビューアのニュートラル姿勢（`idle`）用。ゲームデータ未準備なら 503、セットに無ければ `CLIPSET_NOT_FOUND` |
| GET | `/api/props/{model}` / `.bin` | 小道具メッシュのメタと頂点（§4.5）。リソース同梱の `.ydr` を優先。メタに `textureScope` と解決済み `textures[]` |
| GET | `/api/peds` | ゲームデータ内の ped 一覧（名前・格納形式 `folder` / `component`・分類） |
| GET | `/api/peds/{ped}` | ped の既定部位（スロット・ファイル・頂点数）とボーン数 |
| GET | `/api/ped/{ped}/{component}` / `.bin` | ped のコンポーネント（`head` / `uppr` / `lowr` … のスロット名、またはフォルダ型なら `uppr_003_r` のようなファイル名） |
| GET | `/api/textures/{prop\|ped}/{name}/{texture}.dds` | ディフューズテクスチャ（DDS）。`?format=rgba` で BC1 / BC3 を展開、`?format=gray` でパレットシェーダ用のグレー化 |

`.bin` は `Cache-Control: private, max-age=3600` と `ETag`（辞書ファイルのパス＋サイズ＋スケルトン名）を付ける。ブラウザはまずメタを取り、その `eTag` を `?v=` に付けて `.bin` を取る。URL が内容ごとに変わるので、同じエントリの再選択はブラウザキャッシュで済み、ped を切り替えたときに古い `.bin` が使われることもない（`/api/ped/uppr.bin` のように ped によって中身が変わる URL があるため必須）。

### リソース

| メソッド | パス | 内容 |
|---|---|---|
| GET | `/api/resources` | 設定済みソース（種別・件数・取得日時・進捗）と、既定 3 つのテンプレート |
| POST | `/api/resources` | `{ origin: "folder", path }` または `{ origin: "github", repository, ref, id }`。GitHub は 202 を返して非同期に取得し、進捗は SSE `resource` |
| POST | `/api/resources/{id}/refresh` | GitHub から再取得 |
| POST | `/api/resources/{id}/enabled` | `{ enabled }`。無効にしたリソースはファイルと設定を残したまま一覧から外れる（再ダウンロード不要で戻せる） |
| DELETE | `/api/resources/{id}` | 取得したファイルも削除 |
| GET | `/api/resources/detect?path=` | フォルダの種別判定（`rpemotes` / `scully` / `unknown`）と id の提案 |

既定リソースは `alberttheprince/rpemotes-reborn@master`、`Jerrys-C/rpemotes-reborn-nui@master`、`Scullyy/scully_emotemenu@main`、`Daudeuf/rpemotes@master`（旧 rpemotes）、`andristum/dpemotes@master`。zip は `https://github.com/{owner}/{repo}/archive/refs/heads/{ref}.zip`。

## 4. Core の拡張

### 4.1 カタログ

- `EmoteEntry` に **安定した ID**（`source/category/command`）を追加する。URL とブラウザ側の選択状態に使う
- `CatalogBuilder.Build(dataRoot)` を **複数の `ResourceSource` を受け取る** 形にする。`Source` はリソース ID になる（`rpemotes-reborn` / `rpemotes-reborn-nui` / `scully_emotemenu` を区別）
- DTO は `EmoteEntry` から `CustomYcdPath` などの内部情報を落とし、代わりに `previewable: boolean` と `previewReason: "not-indexed" | "kind" | "animal" | "no-dictionary" | "no-clip" | null` を持たせる。判定は索引完了後（約 1.3 秒）に埋まり、SSE `catalog` の `revision` で再取得を促す。小道具には `available: true | false | null` が付く
- Walks はローダーが「クリップセット名＝辞書名、クリップ名 `walk`」を仮に埋め、索引完了後の判定でゲームの `clip_sets.ymt`（`update.rpf` の PSO ファイル。`fwClipSetManager.clipSets` に 4,524 セット）を引いて置き換える（`Core/Rage/ClipSetTable`）。セットの `clipDictionaryName` の辞書に `walk` があればそれ、無ければ `fallbackId` の連鎖（`move_m@alien` → `move_m@generic` 等）をたどる。273 件中 262 件が再生でき、残りはテーブルに無い名前（リソース側の誤字や MOD 由来）。走り（`run`）は扱わない
- rpemotes の `RP.*` は Lua のハッシュテーブルで順序が不定なので、カテゴリとコマンドを名前順に並べて ID と一覧を決定的にする
- 同梱 `.ycd` の索引はソースごとに持ち、辞書名の衝突はカタログの並び順で先勝ちにする
- 共有エモートは `PartnerCommand`（rpemotes `Shared` の 4 番目、scully `Options.Shared.OtherEmote`。無ければ自分自身）を同じソース・同じカテゴリで引いて `PartnerId` にする。`Placement`（`SharedPlacement`: Offset か Attach）と `StartDelayMs` も読む。DTO には `partnerCommand` / `startDelayMs` / `partner`（相手の id・辞書・クリップ・ループ・遅延・小道具・プレビュー可否・動物 ped・`placement`）が付き、`placement` は主から見た相手の位置に正規化済み（§4.8）

### 4.2 ClipBaker（姿勢の焼き込み）

ブラウザ側で three.js の `Bone` 階層と `AnimationMixer` をそのまま使えるように、**全ボーンのローカル変換** を一定 fps でサンプリングして `Float32Array` に焼く。

```
clip.bin（リトルエンディアン float32 の連結）
  [frame][bone] × (px, py, pz, qx, qy, qz, qw)      frames × boneCount × 7
  [frame]       × (px, py, pz, qx, qy, qz, qw)      frames × 7（hasRootMotion のときだけ）
```

- fps はクリップ本来のレート `(フレーム数 − 1) / 長さ × Rate` を丸めたもの（1〜60）。ボーンデータが 12 MB を超える長尺クリップは fps を落とす。最終フレームは `Duration − ε` でサンプリングし、非ループのクリップが先頭に巻き戻らないようにする
- `PoseSolver.Apply` を通した後の `LocalTranslation` / `LocalRotation` を書き出す。太もものロールボーン補正はここで反映済みになる
- ルートモーション（トラック 5/6）は **別トラック** にして、ブラウザ側のトグルで root ボーンに合成する。焼き直し不要
- スケール（トラック 2）は使用頻度が低いので v1 では焼かない。必要になったらレイアウトのバージョンを上げる
- GTA（Z-up）→ three.js（Y-up）の変換は **ブラウザ側でスケルトンの親グループを X 軸 −90° 回転** して済ませる。座標を個別に入れ替えない
- 辞書のロード結果は LRU（10 件程度）でキャッシュし、同じ辞書の別クリップは即応答

- **ボーンの自由度**: `.yft` のボーンフラグ（RotX/Y/Z, TransX/Y/Z, ScaleX/Y/Z）を `BoneDef.Dofs` に持ち、`PoseSolver` はボーンが許す成分だけをトラックから受け取る（軸ごと）。体のボーンの多くは回転のみで、平行移動トラックはゲームでも無視される。同梱 `.ycd`（例: `bzzz@animation@army1`）には回転のみのボーンへの平行移動や、顔ボーン `FB_*` へのずれた位置が入っていて、全部適用すると顔と脚が歪む
- **顔ボーン**: `FB_*`（freemode・一般 ped）と `FACIAL_*`（主人公・カットシーン ped の高解像度表情リグ。`FACIAL_facialRoot` 配下）はゲーム内では表情レイヤーが毎フレーム上書きするため、体のクリップのトラックは無視する（`PoseSolver.IgnoreFacialBones`、既定オン）。`FACIAL_facialRoot` を無視していなかった頃は、同梱クリップの同ボーンのトラック（120° 回転）で player_zero の顔が頭蓋の中に折り畳まれた。ゲーム自身の表情クリップ（`facials@…`）は別の仕組みで、現状は再生対象外。無視したトラック数はクリップメタの `warnings` に載る
- スケルトンキャッシュ（`cache/skeleton-<ped>.json`）はフラグ込みの版 2 で、旧版は読み捨てて再生成する。`BakedClip.LayoutVersion` も 2 に上げてブラウザキャッシュを無効化した（`FACIAL_*` を無視するようにした 0.2.2 で 3 に）

### 4.3 ゲームデータ索引

- `IGameDataSource` に `HasDrawable` / `LoadDrawable`（`.ydr`、または同名の `.ydd`）と `HasPedComponent` / `LoadPedComponent`（`<pedフォルダ>/<ファイル名>` で索引した `.ydd`）を持つ。小道具（`prop_tool_fireaxe` 等）はゲーム本体のアーカイブにある
- gta-toolkit のアーカイブストリームはスレッドセーフではないため、`GtaToolkitGameData` はエクスポートをロックで直列化する（HTTP 要求と再生可否の確認が同時に走る）
- 索引キャッシュ（必要になったら）: `cache/rpf-index-v1.json`。キーは `GTA5.exe` のサイズ＋更新日時、`dlclist.xml` のハッシュ、各 RPF のサイズ＋更新日時。内容はハッシュ → （アーカイブパス、内部パス）。読み込み時はアーカイブを遅延で開く
- 読めなかった資源はエラーにせず Warning へ。1 件の不良で起動を止めない

### 4.4 リソースソース

- `ResourceSource` = `{ id, path, origin(folder|github), ref }`
- フォルダから種別を自動判定する: `client/AnimationList.lua`（大文字小文字は問わない）があれば rpemotes 系で、その先頭が `DP = {` なら dpemotes（`types.lua` は任意。旧 rpemotes と dpemotes には無い）、`shared/data/` があれば scully。rpemotes 系は 1 つのローダーで読み、`RP` / `DP` のどちらのグローバルでもよい。リストが参照する `Config`（旧リストの `PtfxInfo`）はサンドボックス側のスタブで受ける
- `origin=github` は GitHub の zip（`/archive/refs/heads/<ref>.zip`）を `resources/<id>/` に展開する。取得日時と ref を `source.json` に記録し、「更新」で再取得できるようにする。個人配布のリソースはフォルダ指定で読む
- 既定のリソース 5 つ（rpemotes-reborn / rpemotes-reborn-nui / scully_emotemenu / 旧 rpemotes / dpemotes）の URL は App 側に定数で持つ。アプリにリソースを同梱はしない

### 4.5 Drawable（小道具・メッシュ）

- `MeshExtractor`: gta-toolkit の `Drawable` → `DrawableGeometry` から、`VertexDeclaration` の flags/types に従って **position / normal / uv / (blendWeights, blendIndices)** を取り出す。LOD は最高のみ
- `.bin` のレイアウトは three.js の `BufferGeometry` にそのまま流せる並び（float32 の属性配列を連結し、uint16/32 のインデックスを続ける）。メタ側にオフセット、サブメッシュの範囲、シェーダ名・テクスチャ名を持つ
- テクスチャは第 2 段階。`.ytd` の DDS を gta-toolkit の DDS 出力で取り出し、three.js の `DDSLoader`（S3TC 拡張）で読む。最初は無地のマテリアルで表示する
- 小道具の取り付け: `EmoteProp.Bone`（ボーンタグ）と `Placement`（位置 3 + オイラー角 3、度）を DTO に含め、ブラウザ側で該当 `Bone` の子として配置する。rpemotes / scully は `AttachEntityToEntity(..., rotationOrder = 1)`（Y → Z → X）で取り付ける。この順序は**小道具自身の軸**についての回転（内的回転）で、three.js では Euler 順 `YZX`（行列 Ry·Rz·Rx）になる。外的回転と解釈した `XZY` だと口の葉巻が下を向き、scully の傘が前に垂れる。葉巻・傘・ギターで目視確認済み
- `.bin` のレイアウト: positions (f32 ×3) → normals (f32 ×3、あれば) → uvs (f32 ×2、あれば) → blendIndices (u16 ×4、スキン時) → blendWeights (f32 ×4、スキン時) → indices (u32)。メタに各ブロックの有無と `subMeshes` を持つ
- カメラの「正面」は ped の顔側。GTA の ped は +Y を向き、Z-up → Y-up の回転で three.js の −Z になるので、正面カメラと初期視点、太陽光は −Z 側に置く（当初 +Z に置いていて背中が見えていた）
- ped コンポーネントのブレンド重み・骨番号は D3DCOLOR 型。両方をメモリ順で読む（片方だけ BGRA 変換すると対応がずれる）
- 骨番号が指す先はドロワブル次第。ドロワブルが自前のスケルトンを内蔵していない（freemode の服など）なら ped の `.yft` の index をそのまま指す。内蔵している場合（多くの ped の `head`、ストーリー主人公 3 人・`cs_wade` / `ig_wade` / `cs_stretch` / `ig_tracydisanto` / `mp_f_deadhooker` の全部位）は**内蔵スケルトンの index** で、内蔵側は `.yft` の部分集合だったり順序が違ったりする。`MeshExtractor` が内蔵スケルトンのボーンタグを `MeshData.BlendBoneTags` に載せ、`PedService` が `MeshData.RemapBones` で `.yft` の同じタグの index に付け替える（ゲームがタグで部位を骨に結ぶのと同じ）。付け替えないと静止姿勢では正しく見えるのにエモートで頂点が別の骨に付いて崩れる。DevTools の `skinbones` で影響のある ped を列挙できる（9 体、タグ未解決 0）。この修正で `MeshData.LayoutVersion` を 2 に上げてブラウザキャッシュを無効化した

### 4.6 テクスチャ（M7）

- ディフューズだけ貼る。`MeshExtractor` がシェーダのパラメータから `DiffuseSampler`（`0xF1FE2B71`）を読んで `SubMesh.Diffuse`（名前）と `DiffuseEmbedded`、`PaletteSampler`（`0xAB98831E`）の有無を `Palette` に載せる。埋め込みテクスチャは `MeshData.EmbeddedTextures`
- 解決（`TextureService`）: 小道具は 埋め込み → `<model>.ytd` → 無地。ped はフォルダ型なら `<pedフォルダ>/<名前>.ytd`、単体型なら `<ped>.ytd` の同名テクスチャ。共有 `.ytd`（電話の `Prop_Base_white_full` など）は追わない
- `TextureImage`（Core/Textures）が gta-toolkit の `TextureDX11` を BC1 / BC2 / BC3 / BC4 / BC5 / BC7 / RGBA8 に正規化し、DDS ヘッダ（BC7 は DX10 拡張）を自前で書く。gta-toolkit の DDS / 展開ヘルパはネイティブ依存のスタブなので使わない。ゲームは圧縮ミップを 4×4 までしか持たないので、最後のブロックを複製して 1×1 まで埋める（WebGL はミップ完全性を要求する）
- ブラウザは自前の DDS パーサ（`viewer/textures.ts`）で `CompressedTexture`（S3TC / BPTC）か `DataTexture` を作る。`flipY = false`（DirectX の UV 規約）、`SRGBColorSpace`、アルファ付きは `alphaTest 0.5` + 両面。拡張が無ければ `?format=rgba`（C# の `BcDecoder` が BC1 / BC3 を展開）、それも無理（BC7）なら無地に戻す
- パレットシェーダ（髪・一部の服）はディフューズが強度マップで、色はゲーム内のパレットで決まる。ただし ped シェーダは使わなくても `PaletteSampler` の枠を常に持つので、パラメータの有無では判定できない。`TextureImage.IsIntensityMap`（B チャンネルが空。髪は G に明度、R に毛先ハイライトのマスクを持つ 2 チャンネル形式）でテクスチャの中身から判定し、max(R, G) をグレー化して、該当するものだけ `?format=gray` でグレー化して部位ごとの単色（髪は焦げ茶、他は灰色）でティントする。判定を間違えて服や顔までグレーになった経緯あり
- サブメッシュごとに `geometry.addGroup(…, index)` を切り、マテリアル配列で貼り分ける。「テクスチャ」トグル（`viewer.showTextures`）は配列と単色マテリアルを差し替えるだけ
- アルファを切り抜きに使うかは **シェーダ名** で決める（`ShaderNames`: 既知のシェーダ名を joaat ハッシュにして引く。`alpha` / `cutout` / `decal` / `hair` / `glass` / `fur` を含む名前だけ alphaTest）。素の `ped` シェーダは体テクスチャのアルファをスペキュラマスクに使っており（`a_f_m_beach_01` の体は全面 0.4）、テクスチャ形式で判定すると胴体が消える。未知のシェーダは小道具のみ「アルファ付き形式なら切り抜き」に戻す
- 髪のドロワブルは毛束のカードとは別に、UV を平面投影した低ポリの「殻」ジオメトリを持つ。同じ `ped_hair_spiked` でもシェーダインスタンスが分かれていて、パラメータ `orderNumber`（`0x6063CE32`）が 1（二次パス用）。通常メッシュとして描くと黒いヘルメットのように見えるので、`SubMesh.Hidden` にしてグループを作らない（描かない）
- 布シミュレーション部位: 主人公 3 人やカットシーン ped（`cs_*`）の上着など 33 体 53 部位は `.ydd` と対で `.yld`（クロス辞書、辞書のキーは部位ファイル名の joaat）を持ち、ゲーム内では布シミュレーション（verlet）で動く。索引で `.yld` を持ち（フォルダ型は `<フォルダ>/<ファイル>.yld`、単体型は `<ped>.yld`）、その ped の `cloth` を含むシェーダ名のサブメッシュを `SubMesh.Cloth` / DTO の `cloth` にする。**布ジオメトリの頂点は通常のスキンではない**: 描画頂点（約 4,000）はシミュレーション頂点（約 200、`VerletCloth` の座標＝`CharacterClothController.OriginalPos`）3 つの重心座標で表され、番号スロット 0 / 1 / 3 がシミュ頂点、重みはスロット 1 / 0 / 2 が対応する。番号スロット 2 は 255 か、内蔵スケルトンのボーン番号（袖・襟など、重みスロット 2。その場合 3 つ目のシミュ頂点は重みスロット 3）。これを普通のボーン重みとして読むと重みの合計が 1.5 になり、頂点が原点から 1.5 倍に押し出されて上着が開いて見えた（v0.2.1 まで）。`MeshExtractor.DecodeClothVertex` がシミュ頂点のボーン重み（`BindingInfo` → `BoneIDMap` のタグ、`ClothBinding`）に展開し、上位 4 本に正規化した通常のスキンに変換する。上着は体に固定されて動く（揺れは再現しない）。ビューアの「布」トグル（`viewer.showCloth`、既定オン）で描画グループから外せる（ボタンは読み込んだ部位に布がある時だけ有効）。DevTools の `cloth` で対象一覧と重みの検証、`yld` で辞書の中身を見られる。`.yld` の `Poses`（6 ボーン分の形状）と `EdgeData`（距離拘束）、`BoundComposite`（カプセル）は揺れを実装するときの材料
- 待機表示（エモート未選択）は bind ポーズのルートを Z 軸 180° 回した姿勢にする。`.yft` の bind ポーズは `SKEL_ROOT` に半回転が入っていて −Y を向くが、アニメーションのルートトラックは +Y を向くため、そのままだと「正面」カメラに背中を向ける

### 4.7 ped モデル（M8）

- ped の格納形式は 2 つ。単体型 `<ped>.ydd` + `<ped>.ytd` + `<ped>.yft`（`componentpeds_*.rpf` と DLC の `peds/`）と、フォルダ型 `<ped>/<部位>_<番号>_<r|u>.ydd` + テクスチャ別 `.ytd`（`streamedpeds_*.rpf`。freemode と一部の動物）。`GtaToolkitGameData.ListPeds` は ped の接頭辞（`a_c_` / `a_f_` / `a_m_` / `s_f_` / `s_m_` / `g_f_` / `g_m_` / `u_f_` / `u_m_` / `cs_` / `csb_` / `ig_` / `player_` / `mp_`）を持つ `.yft` のうち、同名フォルダか同名 `.ydd` があるものを返す（1,102 体）
- 既定部位（`PedService`）: 各スロット（`head` / `berd` / `hair` / `uppr` / `lowr` / `hand` / `feet` / `teef` / `accs` / `task` / `decl` / `jbib`）の **番号 0** で 8 頂点を超えるもの。番号 0 が置き場所だけの「無し」（4〜8 頂点）の部位は表示しない。髪だけは次の番号まで試す。単体型のドロワブル名はハッシュなので `PedNaming.DrawableNameCandidates` で復元し、復元できなければ `DiffuseSampler` 名（`uppr_diff_000_a_uni`）から部位と番号を取る
- 人間 ped はボーンタグが共通で、エモートはどの ped にもそのまま載る。動物は別スケルトン。`AnimalPeds` が `creatures@<動物>@` の表 → scully の `PedTypes` → 名前のヒント → 犬 の順で ped を決め、`EmoteEntry.AnimalPed` / `EmoteDto.ped` に載る。rpemotes の `AnimalEmote` は人間側にも付くので、辞書名で動物側だけを拾う
- スケルトンは名前ごとの LRU（`SkeletonService`、`cache/skeleton-<ped>.json` 併用）。`/api/skeleton?ped=` と `/api/emotes/{id}/clip?ped=` で複数を同時に扱い、`AppState` は GTA / 鍵の変更でしか再索引しない。`/api/status` の `skeleton` は設定中の ped のもの
- ビューアの HUD 左端に現在の ped 名のボタンがあり、設定画面と同じピッカー（`shared/PedPicker.tsx`）をポップオーバーで開いて `settings.ped` を書き換える。選んでもポップオーバーは閉じず（続けて試せるように）、検索語・分類は共有ストア + `localStorage`、スクロール位置はメモリに保持して、閉じて開き直しても絞り込みが残る
- ビューアは `activePed`（動物エモートなら `entry.ped`、それ以外は設定の ped）ごとに rig を組み直し、クリップ・小道具・メッシュはその rig に付く。「動物 ped」トグル（`viewer.animalPeds`）を切ると動物エモートも設定の ped で焼く（人間の骨には合わないが、意図的な操作）
- `_p.ydd`（帽子・眼鏡）と `.ymt` の正式バリエーションは未対応

### 4.8 共有エモート（M9）

- rpemotes の `Shared`（配列 4 番目が相手のエモート名、`SyncOffsetSide/Front/Height/Heading` または `Attachto`+`bone`+`pos`/`rot`（`xPos` … の別記法も可）、`StartDelay`）と scully の synchronized_emotes（`Options.Shared.OtherEmote`、`SideOffset/FrontOffset/HeightOffset/HeadingOffset`、`Attach`+`Bone`+`Placement`、`Options.Delay`）を `SharedPlacement`（Offset / Attach）に正規化する。`Placement` は **その ped が相手に対してどこにいるか**: ゲーム側はどちらのメニューも「自分のエモートに Attach があれば自分の ped を相手の骨に貼る」「申請側の ped を自分のエモートのオフセット位置（相手の向き基準、heading 180 = 相手を向く）に置く」で、Attach が両側にあれば申請側だけ貼る
- API は主から見た相手の配置 `PartnerPlacementDto` にまとめる。`attach` は `attached: "main" | "partner"`（どちらが相手の骨にぶら下がるか）と `bone`（-1 = エンティティ原点）・`offset`（小道具と同じ `[x,y,z,rx,ry,rz]`、回転順 `YZX`）、`offset` は `position`（主の座標系: x 右, y 前, z 上）と `heading`（主に対する相対、度）。優先順は 主の Attach → 相手の Attach → 主の Offset（`-Rz(heading)·(side, front, height)`, `+heading`）→ 相手の Offset（`(side, front, height)`, `-heading`）→ 既定 `(0, 1, 0)` / 180°（`explicit: false`）
- ビューア（`viewer/controller.ts`）は `main` / `partner` の 2 スロットに `SkeletonRig`＋`PropLayer`＋`PedMesh`＋`Playback` を持つ。`SkeletonRig` は `frame`（Z-up → Y-up の回転と接地の持ち上げ）と `entity`（GTA 座標系の実体。骨とマネキンはこの下）の 2 層で、Offset は相手の `entity` の位置と Z 回転、Attach は貼り付く側の `entity` を相手の骨（またはエンティティ）の子グループ（小道具と同じ `YZX` のオフセット）に付け替える。骨のオブジェクト名は rig ごとの接頭辞付き（`r0_b12`）で、入れ子になってもミキサーが別 rig の骨を掴まない。スキンメッシュは `bindMode = "attached"` なので `entity` を動かしても追従する
- 時間軸は `Timeline` 1 本（再生・ループ・速度・シーク）。各スロットは「開始遅延の前は最初の姿勢、クリップ内はその時刻、超えたらループ指定なら剰余・無ければ最終姿勢」（`RigSlot.clipTime`）。`Playback` はクロックを持たず、時刻を渡されて `mixer.update(0)` で姿勢を出すだけ。棒人間の更新は貼り付け元 → 貼り付け先の順
- 2 体目の ped は `settings.partnerPed`（null = 主と同じ）、相手側が動物エモートならその動物 ped（`viewer.animalPeds` に従う）。HUD の「相手」トグル（`viewer.showPartner`）、相手 ped のボタン（主と同じピッカー）、詳細パネルの相手へのリンク・配置の説明・開始遅延。相手側を選ぶと主・相手が入れ替わるだけで同じ画になる。手動クリップ選択中は相手を出さない
- 相手が解決しない（`partner: null`、`partnerCommand` は残る）・相手のクリップが無い場合は主側だけを再生して詳細パネルに理由を出す。`devtools shared [--gta-check]` で全ペアの解決状況・配置方式・クリップ有無を一覧できる

### 4.9 エモートの重ね合わせ（M10）

ゲームの 2 スロット再生（プライマリ = 全身 1 本、セカンダリ = その上に重なる 1 本。flag の SECONDARY(32) で決まり、UPPERBODY(16) なら上半身だけ）をビューアで再現する。規則は実機で確認したもの（private の調査メモ §6）。

- **flag**: `EmoteEntry.AnimFlag` を両ローダーで決める。rpemotes は `Flag`（明示）→ `onFootFlag`（`AnimFlag.LOOP`=1 / `STUCK`=50 / `MOVING`=51）→ 旧ブール（`EmoteMoving`→51、`EmoteLoop`→1、`EmoteStuck`→50。EmoteMenu.lua の変換と同じ順）→ 0。scully は `Flags.Stuck and 50 or Flags.Move and 51 or Flags.Loop and 1 or 0`。`Loop` / `Move` / `UpperBody` は flag のビット（LOOPING=1 / SECONDARY=32 / UPPERBODY=16）から導く。MOVING(51) は LOOPING を含むので `Loop = true`（以前は `flag == 1` だけを Loop にしていた）。DTO は `flag` / `slot`（`primary` / `secondary`）/ `upperBody`
- **合成規則**（`viewer/playback.ts` の `Playback`）: 1 つの `AnimationMixer` にスロットごとの `AnimationAction` を持ち、トラックを重ならないように分ける。セカンダリが持つトラックは「上半身マスク（`SKEL_Spine_Root` のサブツリー。`SkeletonRig.upperBodyMask`）∩ セカンダリのクリップが動かすボーン（`meta.animatedBones`）」、プライマリはそれ以外の全ボーン。骨盤・脚・`SKEL_ROOT`（位置も回転も）は常にプライマリ。境界の重みは 0/1 で部分ブレンドはしない
- **ROOT の付け替え**: セカンダリが `SKEL_ROOT` を動かすときは、`Spine_Root` のローカル回転を `inv(プライマリの ROOT 回転) × セカンダリの ROOT 回転 × Spine_Root 自身のローカル回転` に置き換える（`applyReRoot`）。3 つとも焼き込みデータから毎回サンプルする。three.js の `PropertyMixer` は評価値が前回と同じボーンを書き戻さないので、ボーンの現在値に掛けると同じ時刻を再表示するたびに回転が重なる（実装中に踏んだ）。プライマリの ROOT はルートモーション込みのボーンではなくデータから読む（mover の回転は胴体にも掛かるのが正しい）。セカンダリが ROOT を持たないときは付け替えない
- **時間軸**: `Timeline` は 1 本のまま、`RigSlot.timing` をスロットごとに持ち、`clipTime(t, layer)` で各スロットが独立にループ／最終姿勢保持する。プライマリの読み込みだけが時間軸を 0 に戻し、セカンダリは途中参加（ゲームでもセカンダリ開始でプライマリの位相は変わらない）。セカンダリの開始オフセットは未対応
- **ニュートラル = idle**: セカンダリが流れていてプライマリが空のときは、歩き方の既定クリップセット `move_m@generic` / `move_f@generic`（ped 名に `_f_` があれば女性）の `idle` をプライマリとして流す（`/api/clipsets/...`）。動物 ped（`a_c_*`）は対象外（idle が無いので、動物のセカンダリは全身で流す）。何も選んでいないときはクリップを流さず（時間軸も止まる。idle を流していた頃はシークバーが動いて気になった）、rig は従来の「A ポーズ + ROOT 半回転」で立つ。ゲームデータ未準備時と読み込み失敗時も同じ
- **flag がスロットを決める**（`store.ts` / `Viewer.tsx`）: 上段の選択 `selectedId` は flag のスロットで再生する。プライマリなら全身、セカンダリ（flag に SECONDARY。rpemotes / scully とも 51 が大半）なら実機どおり上半身だけを idle の脚の上に流し、クリップの脚トラックは捨てる（S 1,368 件のうち 1,006 件は脚のトラックを持ち、389 件は 10° 超で脚が動く: boxing、selfie8、countdown など。実機で boxing / selfie8 が両メニューとも上半身だけなのを確認済み）。全身を見たいときは「辞書内のクリップを選ぶ」（手動クリップは常に全身）。下段の選択 `secondaryId` はセカンダリ層。**合成オン中は上段にプライマリだけ、下段にセカンダリだけを出す**ので、ゲームで同時に流せない組み合わせ（同スロット同士）は選べない。S を選んだ状態で合成をオンにすると、その S は下段へ移り上段は空になる。経緯: 当初は「主は flag のスロットへ、下段は反対のスロットに自動絞り込み、詳細で上書き」→ 分かりにくいとの指摘で一度「リストがスロットを決める（上段は常に全身）」にしたが、実機と違う絵（S の脚が動く）になるため 2026-09-11 に現行に落ち着いた
- **UI**: 左ペインは上段と下段（検索欄の横の「合成」で開閉、境界はドラッグで移動して `localStorage` に記憶。閉じるとセカンダリは解除、上段の選択はそのまま）。下段の絞り込みは下段自身の検索欄とソース・カテゴリ・種別の選択（上段と同じ 3 つ。`FilterSelects` を共用。上段の絞り込みは効かない）。加えて上段のエモートの ped 種別（`ped`: 動物 ped 名か null = 人間）と一致するものだけを暗黙に出す（犬のエモートは同じ犬のエモートの下でだけ。人間か未選択なら人間のもの）。どちらのリストも選択中の行をもう一度クリックすると解除。バッジは合成オフの上段でセカンダリの行にだけ S を出す（P は情報が無いので出さない。合成オン中は両リストとも均質なので出さない）。詳細パネルに「✕ 解除」（Esc でも主を解除）とセカンダリの行（解除ボタン付き）。トランスポートのフレーム表示は主のクリップ（主が無ければセカンダリ）。小道具は両スロットの分を付ける。共有エモートの相手 ped は従来どおり主に付随（相手 rig はプライマリだけ）
- 検証: private の `tools/diag/preview-res`（既知角度の診断クリップ）を folder resource として読ませ、`tools/shot-layer.mjs` で撮って実機画像と照合した（脚だけ倒れる／胴体だけ倒れる／前傾が直立に戻る／腕だけ／脚・骨盤は動かない／脚 4 秒・腕 3 秒で独立に回る／pushup + crossarms／sit + clap）

## 5. フロントエンド（EmotePreviewer.Web）

FxDeck と同じ **Vite + React + TypeScript + Zustand**。UI ライブラリは使わない。three.js は npm から取り込み、CDN は使わない。

```
src/EmotePreviewer.Web/
  index.html, vite.config.ts（/api を 127.0.0.1:20300 にプロキシ）, tsconfig.json, package.json
  src/
    main.tsx, App.tsx
    shared/      api.ts（fetch ラッパー、SSE 購読）, types.ts（API の DTO）, store.ts（Zustand）, i18n
    catalog/     検索ボックス、絞り込み、仮想リスト、詳細パネル
    viewer/      scene.ts（renderer / camera / OrbitControls / グリッド / テーマ。描画は「変化があったフレームだけ」: 再生が進んだ・カメラが動いた・読み込みや切替が invalidate() を呼んだときに限り renderer.render する。サーバー切断中（オーバーレイ表示中）は setPaused(true) でループごと止める）
                 skeleton.ts（バインドポーズ → Bone 階層 + 棒人間 LineSegments）
                 playback.ts（clip.bin → AnimationClip → AnimationMixer で時刻の姿勢を出す `Playback`、共有の時間軸 `Timeline`）
                 props.ts（.bin → BufferGeometry、ボーンへの取り付け）
                 controller.ts（主・相手の 2 スロット、配置、tick）
                 Viewer.tsx（canvas と操作パネル。スロットごとの読み込みは `useRigSlot`）
    settings/    GTA・鍵・リソース・診断・ログ
    styles.css
```

- **画面は 2 つ**: メイン（左に一覧＋検索、右にビューア、下または右端に詳細）と設定。ルーティングは `/` と `/settings`
- 一覧: 検索語（コマンド・ラベル・辞書・クリップ）、ソース、カテゴリ、種別、プレビュー可否、小道具あり、同梱 `.ycd` で絞り込み。キーボードで上下移動して即プレビュー。連打時は最後のリクエストだけ反映（古い応答は捨てる）
- 詳細: 辞書・クリップ・長さ・ループ／移動フラグ・小道具・退出エモート・プレビュー不可の理由。クリップ不一致のときは辞書内クリップ一覧から選び直せる
- ビューア: 再生／停止、スクラブ、速度、ループ、補助ボーン（`MH_/PH_/IK_/RB_/SM_/EO_`）表示、ルートモーション、カメラプリセット（正面・側面・上）、小道具の表示切替、共有エモートの相手の表示切替と相手 ped の選択
- 重ね合わせ（§4.9）: flag がセカンダリのエモートは単独でも実機どおり上半身だけ（脚は歩き方の idle）。一覧の「合成」で下段を開くと上段はプライマリだけ、下段はセカンダリだけになり、両方選ぶと同時再生。Esc / 「✕ 解除」で主を解除
- three.js の描画は React の外で管理する（`Viewer.tsx` は canvas を 1 つ持ち、`useEffect` で scene を生成・破棄する）。フレームごとの状態を React state に流さない
- テーマは検証用ビューアと同じ CSS 変数方式（システム追従 + 明示指定）
- UI 文字列は日本語を正とし、英語を追随させる（FxDeck の `locales/ja.ts` / `en.ts` と同じ方式）。ハードコードしない

## 6. 初回起動とエラー状態

| 状態 | 画面の表示 | できること |
|---|---|---|
| リソース未設定 | 「リソースを追加」案内。既定 3 つのワンクリック取得とフォルダ指定 | 設定のみ |
| 鍵ファイル無し | KeyTool の案内と鍵フォルダの指定 | 一覧・検索 |
| GTA フォルダ不明 | フォルダ指定（自動検出の結果も表示） | 一覧・検索 |
| 索引構築中 | 進捗バー | 一覧・検索 |
| サーバー停止 | SSE 切断を検知して「EmotePreviewer が終了しました」を表示 | 再起動を促す |
| エントリが再生不可 | 理由（辞書無し／クリップ無し／シナリオ／Walk／動物）を詳細パネルに表示 | 他の操作 |

GTA が無いとスケルトンが取れないため、同梱 `.ycd` だけのプレビューも成立しない。一度 GTA から読んだスケルトンは `cache/` に保存し、以降は索引完了を待たずにビューアを初期化できるようにする（ゲームデータの再配布にはならない範囲の利用）。

## 7. ビルドと配布

- `dotnet build` が `npm ci` + `npm run build` を実行し、`dist/` を `wwwroot/` として exe に埋め込む（FxDeck の `BuildWeb` / `EmbedWeb` ターゲットと `EmbeddedWebRoot` を流用）。`-p:SkipWebBuild=true` で Node 無しビルド
- 開発時は `npm run dev`（Vite、`/api` をプロキシ）と `dotnet run --project src/EmotePreviewer.App -- --no-browser --data-dir <一時フォルダ>` を並走させる
- 配布は `PublishSingleFile` + `SelfContained`（win-x64）。slim 版は任意
- 同梱しないもの: 鍵、ゲームデータ、エモートリソース。`.gitignore` の `*.dat` は維持
- GitHub Actions は **ビルドとフィクスチャ無しテスト** のみ（`build.yml`）。`release.yml` はタグ `v*` で self-contained / slim の単一 exe を GitHub Releases に添付する。GTA 実機を要するテストは開発機でだけ回す
- `IncludeNativeLibrariesForSelfExtract` で NLua の `lua54.dll` も exe に含める（約 62 MB）
- 環境変数 `EMOTEPREVIEWER_WEBROOT` に `dist` を指すと、exe を再ビルドせずにフロントエンドを差し替えられる。UI の確認はヘッドレス Chrome を DevTools Protocol で操作して撮る（`--screenshot` は SSE で止まる）
- バージョンは `Directory.Build.props` で一元管理し、`/api/status` と設定画面に出す
- 第三者ライセンス（gta-toolkit、NLua、three.js、React ほか）は `THIRD-PARTY-NOTICES.md` にまとめ、設定画面から見られるようにする

## 8. 決定事項と保留事項

決定:

- ホストはコンソール exe + Kestrel（`127.0.0.1` のみ）。UI はブラウザ。ウィンドウが欲しければ `--app`
- フロントエンドは Vite + React + TypeScript + Zustand。three.js は npm 同梱
- ブラウザには **ローカル変換を焼いたクリップ** を `.bin` で送り、three.js の `Bone` 階層 + `AnimationMixer` で再生する
- ルートモーションは別トラック、ブラウザ側トグル
- 状態通知は SSE。WebSocket は使わない
- フォルダ選択はテキスト入力＋サーバー側ダイアログ
- 索引キャッシュは入れない（索引 1.1〜1.4 秒、起動から準備完了まで約 1.8 秒）
- テクスチャは表示しない（無地の小道具・マネキン）→ M7 で撤回し、ディフューズだけ貼る
- 共有エモートの配置はゲーム側のコード（rpemotes `Syncing.lua`、scully `main.lua`）の意味に合わせ、「自分のエモートの Attach は自分を相手に貼る」とする
- エモートの重ね合わせは 2 スロット（プライマリ全身 + セカンダリ上半身マスク）で、トラックを分けた 2 つの `AnimationAction`。部分ブレンドはしない。ニュートラルは歩き方の `idle`
- トレイ常駐はしない。ログドロワーも付けない
- API テストは `WebApplicationFactory` ではなく実際の Kestrel を空きポートで起動して行う

保留:

