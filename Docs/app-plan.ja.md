# EmotePreviewer 実装計画

[app-design.ja.md](app-design.ja.md) の設計を、動くものを段階的に出す順序に並べた。各マイルストーンは単独で価値があり、完了時点でリポジトリは常にビルド・起動できる状態を保つ。

## 進捗（2026-09-05 時点）

| M | 状態 | 備考 |
|---|---|---|
| M0 | 完了 | DevTools は `regression` / `coverage` / `find` に加え `bake` / `pose` / `mesh` / `props` / `ped` / `walks` を持つ |
| M1 | 完了 | API テストは `WebApplicationFactory` ではなく、空きポートで実際の Kestrel を起動して検証する（`tests/EmotePreviewer.App.Tests/TestHost.cs`） |
| M2 | 完了 | 全 5,788 件が例外なく焼ける（`devtools bake`）。ブラウザ側のワールド座標が C# の `PoseSolver` と一致することを確認済み |
| M3 | 完了 | 既定リソースは `alberttheprince/rpemotes-reborn@master`、`Jerrys-C/rpemotes-reborn-nui@master`、`Scullyy/scully_emotemenu@main`。**索引キャッシュは入れない**（索引 1.1〜1.4 秒、起動から準備完了まで約 1.8 秒） |
| M4 | 完了 | 500 モデル中 499 が表示可（157 はリソース同梱の `.ydr`）。当初「無い」と判定した 77 モデルは物理付き小道具（瓶・ゴミ箱・カップ）で `.yft` フラグメントに入っており、`.yft` も読むようにして解決した。残る 1 件は同梱の `prop_flagger_sign_02.ydr` が gta-toolkit で読めないもの。**テクスチャは M7 へ** |
| M5 | 完了 | head / uppr / lowr / feet / jbib の既定バリエーションを `SkinnedMesh` で表示。メッシュと棒人間は切替 |
| M6 | 完了 | Walks は `clip_sets.ymt`（PSO）を読んでセット → 辞書（フォールバック連鎖込み）に解決し 262 / 273 件を再生（推定のみだった時は 162 件）。リリースワークフロー（`.github/workflows/release.yml`）と単一 exe 化、README の英日分離（`README.md` 英語 / `README.ja.md` 日本語）と利用者向けへの書き直し（開発者向けは `Docs/development.md`）済み。初回リリースは `v0.1.0` |
| M7 | 完了 | 小道具は 1,083 サブメッシュ中 831 にディフューズが付く（埋め込み 792 + `<model>.ytd` 39。残りは共有 `.ytd` 参照で未解決＝無地）。ped は全部位が付く。BC1 / BC3 / BC7 / RGBA8 を DDS のまま配信し、パレットシェーダ（髪など）は G チャンネルをグレー化して単色でティント |
| M8 | 完了 | 1,102 体（フォルダ型 159、単体型 943）を `/api/peds` で列挙。部位は「番号 0（髪だけ次を試す）で 8 頂点超」を既定にする。動物エモート 221 件はすべて対応 ped に解決（pug 133、rottweiler 65、cat 13、coyote 8、retriever 2）。ped 切替は再索引なし |
| M9 | 完了 | rpemotes 93 件 + scully 1,703 件（同期ダンス 1,643 件は自分自身が相手）の相手を全件解決（`devtools shared`）。配置は貼り付け 91、オフセット 32、既定 1,673。ハグ・おんぶ・CPR・パグを抱える（人間＋動物）を実機で確認 |
| M11 | 完了（2026-09-12） | 旧 rpemotes（`Daudeuf/rpemotes`）と dpemotes（`andristum/dpemotes`）を読む。ローダーは rpemotes 系 1 本のままで、`types.lua` 無し・`Config` 参照・`DP` グローバル・`{"Expression", 名前}` 形式を吸収。実物のクローンで 1,645 件 / 529 件、警告 0。取得テンプレートに 2 つ追加 |
| M10 | 完了（2026-09-11） | 2 スロット再生（プライマリ全身 + セカンダリ上半身）。flag をゲームと同じ規則で読み、`SKEL_Spine_Root` サブツリーのマスクと ROOT の付け替えで合成。既知角度の診断クリップ 8 ケースを実機画像と照合。ニュートラルは `move_m@generic` / `idle` |

リスト型クリップの修正（2026-09-11、M10 の実機比較で発見）: `ClipAnimations`（Type 2）の各要素に生のクリップ時刻を渡していたため、長いシーン用アニメーションの一部区間を指す要素（`friends@frj@ig_1` の `wave_a` = 67.9 秒のアニメーションの 53.2〜56.7 秒など）が先頭から再生され、手を振るはずが腕が上がらなかった。要素ごとに `StartTime + (t × Rate) mod (EndTime − StartTime)` で写すよう修正（`GtClip.Sample`、仕様書 §5.4 も訂正）。焼き込みキャッシュの版数を 4 に上げて古い `.bin` を無効化。`devtools ycd` / `clipinfo` を追加し、GTA 依存テスト `ListClipElementsPlayInsideTheirTimeWindows` で固定。影響範囲は大きく、再生可能 5,836 クリップのうちリスト型が 4,859、そのうち要素の StartTime が 0 でないもの（= 修正前は別の区間を再生していたもの）が 3,443（`devtools bake` が数える）。

太もも roll ボーンの修正（2026-09-11、診断クリップの実機比較で発見）: `RB_L/R_ThighRoll`（骨盤の子、太ももと同じ位置）はゲームがアニメーション後に太ももから決める補助ボーンで、クリップにそのトラックがあっても無視される。`PoseSolver.FixThighRollBones` は「クリップが roll ボーンを動かしていないときだけ」太ももの回転をコピーしていたため、全ボーンにトラックを書く合成クリップ（診断用や変換ツール製）で roll ボーンがバインドに残り、太ももメッシュが引っ張られて脚が曲がって見えた。無条件にコピーするよう変更（`PoseSolverTests`）。

小道具の部品位置の修正（2026-09-12、利用者の指摘「foodtray 系でドリンクがトレイを突き破っている」）: 壊れる小道具（`.yft` フラグメント）はトレイ・カップ・料理を別々の `DrawableModel` として持ち、各モデルは埋め込みスケルトンの骨（`DrawableModel.RootBoneIndex`）の空間で頂点を持つ。ゲームは骨のバインド行列を掛けて描くが、`MeshExtractor` はスキン無しモデルにこの変換を掛けていなかったため、カップ（骨は +0.139 m）が原点に沈んでトレイを貫いていた。v0.1.0 で `.yft` を読むようにした時からの不具合。骨のバインド行列（親子合成）を位置と法線に掛けるよう修正（`MeshExtractor.BoneBindMatrices`、GTA 依存テスト `FragmentPartsSitOnTheirBones`）。影響する小道具は rpemotes-reborn + scully の 500 モデル中 10（食事トレイ 6 種、`prop_bin_07d` の蓋、`prop_snow_sign_road_01a` の標識と支柱、`sf_prop_sf_el_guitar_02a` のヘッド・ネック・ボディ、消火器とサーフボードは骨がゼロ位置で変化なし）。`devtools geom` はフラグメントも読み、`--bones` で骨の平行移動を出す。

描画ループの省力化（2026-09-12、利用者の指摘「『EmotePreviewer が終了しました』の表示中に別ウィンドウの YouTube が重くなる」）: 原因は、全画面の `backdrop-filter: blur` を掛けたオーバーレイの下で three.js が毎フレーム WebGL キャンバスを描き直していたため、Chrome のコンポジタが毎フレーム画面全体のぼかし合成をやり直していたこと（タブが表示中のときだけ起きる = rAF とコンポジタは非表示タブで止まる、という観察と一致）。対処は 2 つ: (1) 切断中は `Scene.setPaused(true)` で rAF ループごと止める（`Viewer` が store の `connected === false` を見て呼ぶ。復帰時に `last` を取り直すので時間が飛ばない）。(2) 姿勢が変わらないフレームは描かない: `Scene.tick` は `onTick`（`ViewerController.tick` がタイムラインの前進か `dirty` を返す）・`OrbitControls.update()` の戻り値（ダンピングが収束するまで true）・`invalidate()` のいずれかが立ったときだけ `renderer.render` する。`invalidate()` はリサイズ・テーマ変更・カメラプリセット・小道具やメッシュやテクスチャの切替・非同期のテクスチャ到着（`TextureCache` のコールバック）から呼ぶ。ヘッドレス Chrome で `renderer.info.render.frame` を数えた結果: 再生中 60 fps、一時停止中 0、カメラプリセット後（収束後）0、サーバー終了後 0。撮影ツール（一時停止 → シーク → プリセット）の出力は変わらない。

実機で触って見つかった修正（同日）:

- 小道具の回転順を `XZY` から `YZX` に訂正（M4 の結果を参照）
- リソースごとの有効／無効チェックボックス（`enabled`、`POST /api/resources/{id}/enabled`）。無効でもファイルは残り、再ダウンロードなしで戻せる
- ped 切り替え時にマネキンの頭だけが出る不具合を修正。`/api/ped/uppr.bin` などが ped に依らず同じ URL でキャッシュ可だったため、ブラウザが男性の頂点データを使い続けていた。`.bin` はメタの `eTag` を `?v=` に付けた URL で取る方式にし、`/api/status` にスケルトン名を載せてビューアがそれを基準に骨組みを作り直すようにした

M7 / M8 で決めたこと（2026-09-05 深夜）:

- ped の既定バリエーションは **番号 0** にする。番号 0 が置き場所だけの「無し」（4〜8 頂点）の部位は表示しない（マスク・バッグ・ベストなどは既定で無しが正しい）。髪だけは次の番号に進む（0 が「無し」＝坊主頭になるため）。freemode の靴は M5 と同じ `feet_001_u`
- テクスチャの URL は `/api/textures/{prop|ped}/{名前}/{テクスチャ}.dds?v=<eTag>`。メッシュのメタに `textureScope` と解決済み `textures[]`（形式・アルファ・パレット・eTag）を載せ、ブラウザはそれだけを取りに行く。`?format=rgba`（S3TC 非対応ブラウザ向けの BC1 / BC3 展開）と `?format=gray`（パレットシェーダ用）は C# 側で展開する
- ゲームは圧縮テクスチャのミップを 4×4 までしか持たない。DDS 書き出し時に最後のブロックを複製して 1×1 まで埋め、WebGL のミップマップ完全性を満たす
- three.js の `DDSLoader` は BC7 と非圧縮の一部を読めないので、自前の小さな DDS パーサ（`viewer/textures.ts`）で `CompressedTexture` / `DataTexture` を作る
- rpemotes の `AnimalEmote` は人間側・動物側の両方に付くため、辞書名で動物側だけを判定する（`creatures@`、`*dog*` / `*cat*` セグメント、`hooman` / `male` / `female` は人間）
- 動物エモートの ped 判定は `AnimalPeds`（`creatures@<動物>@` の表 → scully の `PedTypes` → 名前のヒント → 犬）
- ped 一覧は「ped の接頭辞を持つ `.yft` で、同名フォルダの部位ファイルか同名 `.ydd` があるもの」。パッチアーカイブ（`patchday9ng.rpf` など）にある差し替え ped はパスに `peds` を含まないので、パスでは絞らない

M9 で決めたこと（2026-09-06）:

- 配置の意味はゲーム側のコードに合わせた。rpemotes の `Syncing.lua` と scully の `main.lua` はどちらも「**自分のエモートに `Attachto` / `Attach` があれば自分の ped を相手の骨に貼る**」「**申請した側の ped を、自分のエモートの `SyncOffset*` / `*Offset` の位置（相手の向き基準）に立たせて相手に向ける**」なので、`EmoteEntry.Placement` は「この ped が相手に対してどこにいるか」を表す。計画の完了条件にあった「`carry2` を選ぶと抱えられる側が主の鎖骨に乗る」は逆で、`carry2`（Be carried）を選ぶと **主 ped が相手の** `SKEL_R_Clavicle`（40269）にぶら下がる
- DTO では主から見た相手の配置に正規化する（`PartnerPlacementDto.Resolve`）。主に貼り付け → 主が相手の骨に、相手に貼り付け → 相手が主の骨に、主にオフセット → 相手は主の座標系で `-Rz(heading)·(side, front, height)` に立って `heading` 回る、相手にオフセット → そのまま `(side, front, height)` で `-heading`、どちらも無ければ `(0, 1, 0)` / 180°（`explicit: false`）
- 相手の解決は同じソース・同じカテゴリのコマンド名（大文字小文字を無視）。scully は `synchronized_emotes` に `carry2` という s 無しのコマンドがあるので、`scarried2 → carry2` も解決する。rpemotes の `ccat` / `ccat2` は 4 番目が無く自分自身が相手
- 時間軸はビューアに 1 本（`Timeline`）。長さは各 rig の「開始遅延＋クリップ長」の最大で、遅延前は最初の姿勢、クリップが終わったらループ指定なら巻き戻し、無ければ最終姿勢で保持。`carry`（`missfinale_c2mcs_1`、103 秒）のように片側が極端に長いペアもそのまま
- 2 体目の ped は設定 `partnerPed`（null = 主と同じ。設定画面と HUD の「partner」ボタンから変更）。相手側が動物エモートならその動物 ped。手動クリップ選択中は相手を出さない
- 相手が見つからない・相手のクリップが無いペアでも主側は単独でプレビューし、詳細パネルに理由を出す（主の `previewable` は変えない）

M6 の残り（2026-09-06）: Walks を `clip_sets.ymt` で解決（262 / 273 件）、README を `README.md`（英語）と `README.ja.md`（日本語）に分けて利用者向けに書き直し、開発者向けの内容は `Docs/development.md` / `development.ja.md` へ。設計ドキュメントは `.ja.md` に改名。鍵ツールは GitHub Releases でバイナリ配布する（`EmotePreviewerKeyTool` の `release.yml`）。初回リリースは `v0.1.0`。

## 全体像

| M | 内容 | 完了時にできること |
|---|---|---|
| M0 | リポジトリ整理 | 検証用コードの整理、Web プロジェクトの雛形、CI ビルド |
| M1 | サーバーと一覧 | exe を起動するとブラウザが開き、エモートを一覧・検索・詳細表示できる |
| M2 | プレビュー | 選んだエモートを棒人間で再生できる（検証時と同等の 97%） |
| M3 | リソース管理 | リソースをアプリから取得・追加・更新できる |
| M4 | 小道具 | 小道具付きエモートで小道具が正しい位置に出る |
| M5 | キャラクターメッシュ | 棒人間の代わりに freemode ped のメッシュで再生できる |
| M6 | 仕上げとリリース | Walks の再生、単一 exe、初回リリース |
| M7 | テクスチャ | 小道具とキャラクターにゲームのテクスチャが貼られる |
| M8 | ped モデルの選択 | freemode 以外の ped（FiveM の ped 一覧にあるもの）と、動物エモート用の動物 ped で再生できる |
| M9 | 共有エモート | ハグ・おんぶなど 2 人で行うエモートを、相手側の ped も含めて 2 体で再生できる |
| M10 | エモートの重ね合わせ | 座る＋拍手、腕組み＋歩くなど、ゲームの 2 スロットで同時に再生される組み合わせをそのまま見られる |

M1〜M2 が最小限の成果物。M3 以降は順序を入れ替えてよい（M4 と M5 は独立）。M7 と M8 は 2026-09-05 に追加した（M6 の残りとは独立に進められる）。

## M0: リポジトリ整理

検証用のコードは最終的にリポジトリに残さない方針。ただし回帰テストのフィクスチャ生成は今後も必要なので、開発ツールとして残す。

| 現在 | 処遇 |
|---|---|
| `src/EmotePreviewer.Poc` の `regression` / `coverage` / `find` | `tools/EmotePreviewer.DevTools` に移す（コンソール、`net10.0-windows`）。`coverage` はリソース更新時の確認用、`find` はトラブル調査用 |
| `src/EmotePreviewer.Poc` の `catalog` / `gta` / `pose` / `dump` / `ycd` | 削除。アプリの一覧・詳細・ビューアで代替される。`dump` の JSON 形式も使わなくなる |
| `src/EmotePreviewer.Viewer/viewer.html` | M2 で `Web` に移植した後に削除。CSS 変数・テーマ・再生 UI の作りは引き継ぐ |
| `tools/bundle_viewer.py` | M2 で削除 |
| `Program.cs` の `DetectGta` / `FindDataRoot` | Core の `GtaLocator` / `ResourceSource` に移す（App と DevTools の両方で使う） |
| `tests/EmotePreviewer.Fixtures` | 継続。DevTools からのみ参照 |

タスク:

1. `tools/EmotePreviewer.DevTools` を作り、上記コマンドを移す。`.slnx` を更新
2. `GtaLocator`（レジストリ検出）と鍵フォルダ解決を Core に移す
3. `Directory.Build.props` でバージョンと共通プロパティを一元化。`global.json` で SDK を固定
4. `src/EmotePreviewer.Web` の雛形（Vite + React + TS + Zustand、three.js）。`npm run build` が通る空の SPA
5. `src/EmotePreviewer.App` の雛形（コンソール exe、Kestrel、`EmbeddedWebRoot`、`BuildWeb` / `EmbedWeb` ターゲット、`SkipWebBuild`）。`/api/status` だけ返す
6. `.gitignore` に `node_modules/`、`dist/`、`src/generated/` を追加
7. GitHub Actions: Node セットアップ → `dotnet build` → `dotnet test`（フィクスチャ無しなので回帰テストはスキップされる）
8. README を「アプリ本体の構成」に書き換える。DevTools は開発者向けの節へ。`THIRD-PARTY-NOTICES.md` を作る

完了条件: `dotnet build` が Web ビルド込みで通り、exe を起動すると空ページと `/api/status` が返る。`DevTools regression` で従来どおりフィクスチャが生成され、`dotnet test` が 7/7 通る。

結果: 完了。テストは Core 23 件・App 11 件に増えた（GTA と鍵が無い環境ではゲームデータ依存のものがスキップされる）。

## M1: サーバーと一覧

目標: exe を起動するとブラウザが開き、1 秒以内にエモート一覧が出て、検索・絞り込み・詳細表示ができる。GTA の索引はバックグラウンドで走り、状態が見える。

タスク:

1. `Program`: 引数解析（`--port` / `--data-dir` / `--gta` / `--keys` / `--no-browser` / `--app` / `--verbose`）、単一インスタンス、ポート衝突時のフォールバック、ブラウザ起動、Ctrl+C
2. Kestrel を `127.0.0.1` のみに bind、`Host` ヘッダ検査、SPA フォールバック（`/api` 以外は `index.html`）
3. ログ: コンソール + `logs/` ローリング。起動時に URL・GTA・鍵・索引件数を表示
4. Settings: `settings.json` の読み書き、既定値の解決（レジストリ、環境変数、`data-dir`）。`GET/PUT /api/settings`、`GET /api/diagnostics`、`POST /api/dialogs/folder`、`POST /api/quit`
5. `CatalogBuilder` の複数ソース化と安定 ID、DTO。`GET /api/catalog`
6. AppState: GTA 初期化をバックグラウンドで実行し、`GET /api/status` と SSE `status` で `missingGta` / `missingKeys` / `indexing` / `ready` / `error` を流す。索引完了後に `previewable` 判定を埋めて SSE `catalog` を送る
7. Web: `api.ts`（fetch ラッパー、SSE 購読、切断検知）、`store.ts`、ルーティング
8. Web: 一覧画面。検索語、ソース／カテゴリ／種別／プレビュー可否／小道具の絞り込み、仮想リスト、キーボード操作、詳細パネル。ビューア領域はプレースホルダー
9. Web: 設定画面。GTA フォルダ、鍵フォルダ、リソースフォルダ（M1 ではフォルダ指定のみ）、診断情報、終了ボタン
10. テスト: `tests/EmotePreviewer.App.Tests` で `/api/status`・`/api/catalog`・`/api/settings` を検証（GTA 無し環境でも通るもの）。`WebApplicationFactory` は使わず、`AppHost.StartAsync` で空きポートに実際の Kestrel を立てる（FxDeck と同じ方式）

完了条件:

- リソースが 2 つ設定された状態で起動 → ブラウザに一覧表示まで 1 秒以内
- 6,532 件から任意の語で検索し、絞り込みが即時に反映される
- 鍵ファイルを外すと「鍵が無い」状態になり、一覧は使える
- 索引完了後、Animation 種別の各エントリに再生可否と理由が付く
- 2 つ目の exe を起動すると既存の URL がブラウザで開くだけで終了する

## M2: プレビュー

目標: 検証時に確認した 5,896 件を、ブラウザ内で棒人間として再生できる。

タスク:

1. `ClipBaker`（Core）: `IClip` + `PoseSolver` → `BakedClip`。fps はクリップ本来のレート（上限 60）、ルートモーションは別トラック
2. `ClipService`（App）: 辞書の LRU キャッシュ、`GET /api/skeleton`、`GET /api/emotes/{id}/clip`、`GET /api/emotes/{id}/clip.bin`（`ETag` / `Cache-Control` 付き）、`GET /api/dictionaries/{name}/clips`、`GET /api/clips/{dict}/{clip}.bin`
3. Web `viewer/skeleton.ts`: バインドポーズから `THREE.Bone` 階層を組み、Z-up → Y-up は親グループの回転で処理。`SKEL_*` を結線した棒人間、補助ボーンはトグル
4. Web `viewer/playback.ts`: `clip.bin` → `AnimationClip`（`VectorKeyframeTrack` + `QuaternionKeyframeTrack`）→ `AnimationMixer`。再生／停止、スクラブ、速度、ループ、ルートモーション合成
5. Web `viewer/scene.ts` / `Viewer.tsx`: renderer、OrbitControls、グリッド、カメラプリセット、テーマ連動、リサイズ。検証用ビューアの UI を移植
6. 一覧の選択に追従して自動でロード・再生。`AbortController` で古いリクエストを打ち切る
7. スケルトンを `cache/` に保存し、次回起動時は索引完了前に `/api/skeleton` を返せるようにする
8. 詳細パネルからの手動クリップ選択（辞書内一覧）
9. テスト: `ClipBaker` の出力を回帰フィクスチャの姿勢と突き合わせる（既存の `DecoderRegressionTests` と同じ土台）。`clip.bin` のレイアウトをテストで固定する

完了条件:

- DevTools の `coverage` が再生可と判定する全エントリが、`ClipBaker` で例外なく焼ける（DevTools 側に「全件を焼いて例外を数える」オプションを足して確認）
- 選択から再生開始まで 0.5 秒以内（辞書キャッシュ済みなら 0.1 秒以内、ブラウザキャッシュ済みなら即時）
- 17 秒のダンス、10 秒の同梱 `.ycd`、89 秒のホールドポーズの 3 件で、検証時と同じ姿勢になる
- ここで `src/EmotePreviewer.Viewer` と `tools/bundle_viewer.py` を削除

結果: 完了。補足として、コミュニティ製のホールドポーズは Rate 1/24 で 90 フレームを 89 秒に伸ばしているため本来のレートが 1 fps になり、そのまま 1 fps で焼くのが正しい（three.js が補間する）。長さ 0 のクリップが数件あり、1 姿勢として焼く。

リスク:

- three.js の `QuaternionKeyframeTrack` は球面線形補間を行うので、高速回転のフレーム間で検証時（最近傍フレーム）と見た目が僅かに変わる。問題なら fps を上げる
- 89 秒 × 60 fps のような大きな `.bin`（20 MB 超）はメモリを食う。fps の上限を下げるか、ホールドポーズをフレーム間引きで検出する

## M3: リソース管理

目標: リソースをアプリだけで揃えられる。個人カスタマイズ済みのフォルダも読める。

タスク:

1. `ResourceSource` の種別自動判定（rpemotes 系 / scully）
2. `ResourceManager`（App）: GitHub の zip を `resources/<id>/` に展開、`source.json` に ref と取得日時。進捗は SSE `resource`。`/api/resources` の GET / POST / DELETE と `refresh`
3. 設定画面のリソース一覧: 既定 3 つのワンクリック追加、フォルダ追加、削除、更新、並び替え（辞書名の衝突は先勝ち）
4. 初回起動の案内画面（リソース未設定時）
5. リソース変更後のカタログ再構築と SSE `catalog`
6. 起動時間を計測し、2 秒を超える環境向けに索引キャッシュを実装するか判断

完了条件: `--data-dir` に空のフォルダを指定して起動し、ブラウザ内の操作だけで既定 3 リソースを取得し、一覧・プレビューが動く。

結果: 完了。GitHub の zip は rpemotes-reborn 系が約 75 MB、scully_emotemenu が約 25 MB（codeload は Content-Length を返さないので進捗は転送量のみ）。索引は 1.1〜1.4 秒、起動から準備完了まで約 1.8 秒なので索引キャッシュは入れない。

## M4: 小道具

目標: 小道具付きエモート（約 1,300 件）で小道具が正しい位置・向きに表示される。

タスク:

1. `GtaToolkitGameData` の索引に `.ydr` / `.ydd` を追加。リソース同梱の `stream/*.ydr` も索引する（同梱優先）
2. `MeshExtractor`: `VertexDeclaration` に従って position / normal / uv を取り出し、`MeshData` にする。LOD は最高のみ。読めない頂点形式は警告にして無地の箱で代替
3. `MeshService` と `GET /api/props/{model}` / `.bin`。モデル名で LRU キャッシュ、`ETag`
4. Web `viewer/props.ts`: `BufferGeometry` 化、`EmoteProp.Bone` のボーンに `Placement` で取り付け。回転の適用順は rpemotes / scully の実装に合わせ、代表的なエモート（傘、ギター、電話）で目視確認
5. 小道具の表示切替、第 2 小道具（`SecondProp`）対応
6. テクスチャ（任意）: `.ytd` → `GET /api/textures/{name}.dds` → `DDSLoader`。無地でも十分なら見送る

完了条件: 小道具ありエントリのうち、モデルが見つかるものは全て表示される。見つからないものは詳細に理由を出す。

結果: 完了（テクスチャは M7 へ）。`.ydr` はファイル名のハッシュで索引し、リソース同梱の `stream/*.ydr` を優先する。`.ydd` は名前一致のときだけ使う（全 10,586 辞書の走査に 15 秒かかるが、見つからないモデルは辞書内にも無いので走査は不要と判断）。`.ydr` / `.ydd` に無い 77 モデルはすべて `.yft` フラグメント（`FragType.PrimaryDrawable`）だったので、`LoadDrawable` は `.yft` にもフォールバックする。これで 500 モデル中 499 が解決する。回転は `AttachEntityToEntity` の rotationOrder 1（Y → Z → X）を小道具自身の軸についての回転として three.js の Euler 順 `YZX` で適用する。最初は外的回転（`XZY`）と解釈していたが、口の葉巻が下を向き scully の傘が前に垂れたので、両者を比較して `YZX` に確定した。

## M5: キャラクターメッシュ

目標: 棒人間の代わりに freemode ped のメッシュで再生できる。棒人間との切替可能。

タスク:

1. `mp_m_freemode_01` の各コンポーネント `.ydd`（head / uppr / lowr / feet / hand 等）の既定バリエーションを決めて読む
2. `MeshExtractor` にスキニング（blendWeights / blendIndices）を追加。ボーンインデックスはスケルトンのタグ順に合わせて変換
3. `GET /api/ped/{component}` / `.bin`。Web 側で `SkinnedMesh` + `Skeleton`（M2 の `Bone` 階層を共用）
4. 無地マテリアル（マネキン）で表示。テクスチャは M4 の結果次第
5. 女性スケルトン（`mp_f_freemode_01`）の選択肢。ボーン構成は同じなのでメッシュだけ差し替え

完了条件: 代表的なダンス・座り・小道具エモートで、メッシュが棒人間と同じ姿勢になり、手足の貫通が目立たない。

結果: 完了。コンポーネントの `.ydd` はどの ped でも同じファイル名（`uppr_000_r.ydd` など）なので、`<pedフォルダ>/<ファイル名>` で索引する。既定は head_000_r / uppr_000_r / lowr_000_r / feet_001_u / jbib_000_u（無ければ次の番号）。ブレンドの重みと骨番号はどちらも D3DCOLOR 型で、片方だけ BGRA 入れ替えを行うと対応がずれてメッシュが崩れる（両方メモリ順で読む）。

## M6: 仕上げとリリース

タスク:

1. Walks: `clip_sets.ymt` を gta-toolkit の PSO パーサで読み、クリップセット → 歩行クリップ（`walk` / `run`）を解決して再生。解決できないものは名前表示に留める
2. Scenario / Expressions / 動物: 名前と理由の表示を整える（再生はしない）
3. 「クリップ名が違う」148 件の内訳を確認し、必要なら名前の補正表を持つ
4. エラー画面・ログ出力の整備。サーバー停止時の表示
5. `dotnet publish`（`PublishSingleFile` + `SelfContained`）、GitHub Releases への添付、README の利用者向け節（鍵の作成手順は KeyTool のリポジトリへリンク）、`README.md`（英語）と `README.ja.md` の分離
6. 起動時間・メモリの計測と、索引キャッシュの最終判断
7. トレイ常駐にするかの判断（コンソールで困らなければしない）

完了条件: 初回リリース（v0.1）。README の手順どおりに他の PC で動く。

状態: Walks は `clip_sets.ymt` 経由で 262 / 273 件を再生できる（セット自身の辞書 220 件、フォールバック 38 件、名前一致のみ 4 件。残り 11 件はテーブルに無い名前: `move_m@prison_gaurd`、`move_m@prisoner_cuffed`、`move_heist_lester`、`move_m@bag`、`clipset@move@trash_fast_turn`、`clipset@anim@ingame@move_m@zombie@core` で、いずれもゲーム側に定義が無い）。`devtools walks [--list]` と `devtools clipsets <set>` で確認できる。

`clip_sets.ymt` の所在と形: `x64a.rpf\data\anim\clip_sets\clip_sets.ymt` と `update\update.rpf\x64\data\anim\clip_sets\clip_sets.ymt` の 2 つだけで、DLC 分は update 側に統合されている（DLC の `dlc.rpf` にクリップセット定義は無い）。ルートは `fwClipSetManager`、`clipSets` は名前ハッシュをキーにした `fwClipSet` のマップで、各セットは `clipDictionaryName`（辞書名ハッシュ）、`fallbackId`（別セット）、クリップ項目のマップ（フラグ・優先度・ボーンマスク）を持つ。セット自身の辞書に無いクリップは項目に載っていなくてもフォールバック先から供給される（`move_m@gangster@var_a` は `walk` を持ち `run` は `move_gangster` 側）。辞書名はハッシュしか無いので、索引済みの `.ycd` のファイル名から逆引きする。gta-toolkit の `PsoReader` がそのまま読める。

「見つからない」ものの内訳（2026-09-05、DevTools `missing`）。FiveM 側に組み込まれたアセットを参照している可能性を疑ったが、FiveM はゲームデータを配布せずクライアントの GTA V を読むだけで、サーバー成果物（build_server_windows）にもモデルやアニメーションは含まれない。内訳は次のとおりで、いずれもゲーム本体側で説明がつく:

- 小道具 77 件 → `.yft` フラグメント。読むようにして解決済み
- 辞書が無い 40 件 → すべて Walk のクリップセット名（`move_m@gangster@var_a` 等）。`clip_sets.ymt` で辞書とクリップに解決すべきもの
- クリップが無い 113 件（動物以外）→ (a) Walk で辞書はあるがクリップ `walk` が無いもの（`move_m@alien` には `alien_run` / `run` しか無い等。これも `clip_sets.ymt` の解決対象）、(b) scully のナイトクラブダンスの `_flats_` 版（`hi_dance_facedj_13_v1_flats_female^4` 等 約 60 件）は本体・島版のどちらの辞書にも存在しない、(c) `missheistdocksprep1hold_cellphone / static`、`amb@bagels@male@walking@ / idle_a`、`rcmjosh1 / keeper_base` などはリソース側の定義がゲームの実クリップ名と食い違っているもの。(b)(c) はゲーム内でも再生されない（小道具だけ付く）はずで、アプリ側は「クリップが無い」と表示するのが正しい。将来は「クリップ無しでも小道具だけ表示する」扱いにしてもよいScenario / Expressions / 動物は理由付きで表示のみ。`dotnet publish` は `IncludeNativeLibrariesForSelfExtract` で `lua54.dll` も exe に含め、約 62 MB の単一 exe になる。`release.yml` はタグ `v*` で self-contained と slim の 2 種を GitHub Releases に添付する。トレイ常駐はしない。README は英語版（`README.md`）と日本語版（`README.ja.md`）に分けた。初回リリースは `v0.1.0`。

## M7: テクスチャ

目標: 小道具とキャラクター（マネキン）にゲームのディフューズテクスチャが貼られ、無地より判別しやすくなる。法線・スペキュラ・パレットは対象外。

調査結果（2026-09-05、DevTools `tex` / `pedtex` / `ytd`）:

- 小道具の `.ydr` は多くがディフューズを **埋め込み**（`ShaderGroup.TextureDictionary`）で持つ。傘 `p_amb_brolly_01` は 3 枚、ギター `prop_acc_guitar_01` は 2 枚（512² DXT1）、葉巻は 1 枚。シェーダのパラメータのうち `DiffuseSampler`（ハッシュ `0xF1FE2B71`）が貼るべきテクスチャで、埋め込み（`TextureDX11`）か外部参照（名前だけの `Texture`）かが分かる
- 外部参照もある。電話 `prop_phone_ing` は `Prop_Base_white_full` / `Prop_Phone_details_2` / `phone_screen` を共有 `.ytd` から引く。共有 `.ytd` の所在は ytyp の `txdName` と `gtxd.ymt` の親子関係で決まり、これを追うのは別作業
- ped コンポーネントのディフューズは **すべて外部参照**（`uppr_diff_000_a_whi`、`jbib_diff_000_a_uni` など）。法線とスペキュラは埋め込み。freemode や動物などの「フォルダ型」ped では `<pedフォルダ>/<テクスチャ名>.ytd`（1 辞書 1 枚）、`a_m_y_business_01` のような「単体型」ped では `<ped>.ytd` に全部（41 枚）入っている。`_r` 部位は人種別（`_whi` / `_bla` / `_chi` …）、`_u` 部位は `_uni`
- 形式は DXT1（BC1）が大半、DXT5（BC3）、パレットなど一部に非圧縮 `A8R8G8B8`、新しい DLC に BC7。gta-toolkit の展開・DDS 書き出し（`TextureCompression` / `DDSIO`）はネイティブ DirectXTex 前提の **スタブ** なので使えない。DDS コンテナは 128 バイトヘッダ（BC7 は DX10 拡張ヘッダ）を自前で書けば十分で、three.js の `DDSLoader` が DXT1 / DXT3 / DXT5（`WEBGL_compressed_texture_s3tc`）と BC7（`EXT_texture_compression_bptc`）を GPU 側で展開する
- 衣服の一部はパレットシェーダ（`PaletteSampler` `0xAB98831E` と `*_pall_*` テクスチャ）で色を付けるため、ディフューズをそのまま貼ると色が違う。v1 では許容する
- `.ytd` はゲーム全体で 40,023 個。索引（名前ハッシュと `<フォルダ>/<名前>`）は追加済みで、起動時間への影響は無視できる

タスク:

1. Core `TextureExtractor`: `TextureDX11` → DDS バイト列（全ミップ含む）。`MeshData` のサブメッシュにディフューズのテクスチャ名と `Embedded` フラグを持たせる
2. テクスチャの解決: 小道具は 埋め込み → `<model>.ytd` → 見つからなければ無地。ped は フォルダ型なら `<pedフォルダ>/<名前>.ytd`、単体型なら `<ped>.ytd` 内の同名テクスチャ。人種は `whi`、バリエーションは `a` を既定にする
3. App `TextureService` と `GET /api/textures/{scope}/{name}.dds`（`scope` は `prop:<model>` / `ped:<folder>`）。LRU、`ETag`（`?v=` 方式）、`Content-Type: image/vnd-ms.dds`
4. Web: メッシュのメタにある `subMeshes[].diffuse` を `DDSLoader` で読み、`geometry.addGroup` ごとに `MeshStandardMaterial({ map })` を割り当てる。`SRGBColorSpace`、DXT5 は `alphaTest` で切り抜き、`flipY = false`。読めないときは無地に戻す
5. 拡張が無い環境（`WEBGL_compressed_texture_s3tc` 非対応）向けに、C# で BC1 / BC3 を RGBA に展開する経路を用意する（BC7 は非対応のまま無地）
6. 「テクスチャ」トグル（`viewer.showTextures`、既定オン）と、テクスチャが外部参照で解決できなかったことを詳細に表示
7. テスト: DDS ヘッダのレイアウト、`p_amb_brolly_01` の埋め込み 3 枚が取り出せること、`mp_m_freemode_01/head_diff_000_a_whi` が解決できること（GTA 依存はスキップ可）
8. DevTools `props --textures`: カタログが参照する全モデルでディフューズが解決できる割合を出す

完了条件: 傘・ギター・葉巻・電話とマネキン（head / uppr / lowr / feet / jbib）にテクスチャが貼られる。読めないテクスチャは警告 1 行で無地に戻り、例外で止まらない。

結果（2026-09-05）: 完了。傘・ギター・freemode・単体型 ped・動物で目視確認。電話は共有 `.ytd`（`Prop_Base_white_full` など）を参照するため本体は無地、埋め込みの画面パレットだけ付く。三段構え（GPU 圧縮テクスチャ → C# で RGBA 展開 → 無地）で例外は出ない。

## M8: ped モデルの選択と動物

目標: マネキンを freemode 以外の ped（[FiveM の ped 一覧](https://docs.fivem.net/docs/game-references/ped-models/) にあるもの）に差し替えられる。動物エモートは動物の ped で再生できる。

調査結果（2026-09-05）:

- ped は 2 種類の格納形式がある。**単体型**（`componentpeds_*.rpf/<ped>.ydd` + `<ped>.ytd` + `<ped>.yft`。`a_m_y_business_01` は 8 ドロワブル・41 テクスチャ）と **フォルダ型**（`streamedpeds_*.rpf/<ped>/uppr_000_u.ydd` … + テクスチャごとの `.ytd`。freemode と動物 `a_c_rottweiler` はこちら）。DLC の ped は `update/x64/dlcpacks/*/.../peds/` にある。かぶり物などは `pedprops.rpf/<ped>_p.ydd`
- 単体型の `.ydd` 内のドロワブル名はハッシュで、`head_000_r` / `jbib_000_u` / `hair_001_r` は名前から復元できたが `uppr` / `lowr` の 2 つは復元できなかった。代わりに各ドロワブルの `DiffuseSampler` 名（`UPPR_DIFF_000_A_UNI` 等）から部位と番号を判定できる。正式には `<ped>.ymt`（`CPedVariationInfo`、PSO）に部位ごとのドロワブル数・テクスチャ数があり、gta-toolkit の `PsoReader` で読める
- 人間の ped はボーンタグ（`SKEL_*`）が共通なので、どの ped でも同じエモートがそのまま再生できる。体型はその ped の `.yft` のバインドポーズに従う
- 動物は別スケルトン。`creatures@rottweiler@amb@world_dog_barking@idle_a / idle_a` は `a_c_rottweiler.yft`（82 ボーン）で 74 タグ中 40 が一致（freemode では 30）。一致しない残りは yft に無い顔・補助ボーン。辞書名の `creatures@<動物>@` から既定 ped を決められる（rottweiler / retriever / husky / shepherd / chop は同じ「大型犬」スケルトン、pug / poodle / westy は小型犬、cat、rabbit、pig / boar、deer、coyote、mtlion、cow、hen …）。scully は `PedTypes = {'big_dogs'}` も持つ
- 現状 `AppState` はスケルトンを 1 つしか持たず、ped を変えると索引からやり直す（約 1.5 秒）。複数スケルトンを同時に扱う作りに変える必要がある

タスク:

1. `SkeletonService`: yft 名 → `SkeletonDef` の LRU と `cache/skeleton-<name>.json`。`GET /api/skeleton?ped=`、`/api/emotes/{id}/clip` は焼き込み先スケルトンをクエリで受ける（`ETag` に含める）。`AppState` の再索引は GTA / 鍵の変更時だけにする
2. ped 一覧: 索引済み `.yft` から ped らしい名前（`a_c_` / `a_f_` / `a_m_` / `s_f_` / `s_m_` / `g_f_` / `g_m_` / `u_f_` / `u_m_` / `cs_` / `csb_` / `ig_` / `mp_`）を集めて `GET /api/peds` で返す（接頭辞でカテゴリ分け、格納形式、部位の有無）。FiveM の一覧は名前とカテゴリの参照元
3. `PedService`: 単体型・フォルダ型の両方から部位ごとの既定ドロワブルを選ぶ（番号が最小で頂点数が 8 より多いもの。単体型は `DiffuseSampler` 名で部位判定、できれば `.ymt` を読んで正式なバリエーションにする）。`GET /api/ped/{ped}/{component}` に変更
4. 動物: エモートの辞書名 `creatures@<動物>@` と scully の `PedTypes` から動物 ped を決める表。動物エモートを選ぶとビューアがその ped のスケルトンとメッシュに切り替わり、`previewReason: animal` を廃止して再生可にする
5. Web: 設定に ped の検索付きピッカー（カテゴリ、名前、部位数）。ビューアは ped ごとに rig を持ち替える。「動物エモートは動物 ped で表示」トグル
6. `_p.ydd`（帽子・眼鏡）と `.ymt` の正式バリエーションは後回し。テクスチャは M7 の仕組みをそのまま使う（単体型は `<ped>.ytd`）
7. テスト: `a_m_y_business_01` の部位判定、`a_c_rottweiler` のスケルトンで動物クリップが焼けること（GTA 依存はスキップ可）、`/api/peds` のカテゴリ分け

完了条件: `a_m_y_business_01`（単体型）と `s_m_y_cop_01`（フォルダ型）でダンスが再生でき、犬のエモート（`bdogbark` 等）が `a_c_rottweiler` で崩れずに再生される。ped の切替に再索引が要らない。

結果（2026-09-05）: 完了。`s_m_y_cop_01` は実際には単体型（`patchday9ng.rpf` で差し替え）だったので、フォルダ型の確認は freemode と `a_c_rottweiler` で行った。`_p.ydd`（帽子・眼鏡）と `.ymt` の正式バリエーションは未対応のまま。

## M9: 共有エモート（2 体再生）

目標: 共有エモート（rpemotes の `Shared`、scully の synchronized_emotes / synchronized_dance_emotes）を選ぶと、相手側のエモートを再生する 2 体目の ped が正しい相対位置に出る。

調査結果（2026-09-05）:

- rpemotes-reborn の `RP.Shared` は 93 件。配列の 4 番目が相手側のエモート名（`hug` ↔ `hug2`）。位置は 2 方式で、`SyncOffsetFront` / `SyncOffsetSide` は「相手の向き基準で (横, 正面) の位置に立ち、相手に向く（heading − 180°）」（12 件）、`Attachto` + `bone` + `pos` + `rot` は「自分の ped 全体を相手の骨に貼り付ける」（おんぶ・抱え上げなど 33 件）。後者は小道具と同じ `ATTACH_ENTITY_TO_ENTITY` の座標系で、回転順も同じ `YZX`
- scully_emotemenu は synchronized_emotes に 60 件（+ 生成されるダンス）。`Options.Shared` に `OtherEmote`、`FrontOffset` / `SideOffset`（16 / 3 件）、`Attach` + `Bone` + `Placement`（13 件）で、名前が違うだけで rpemotes と同じ構造
- 現在のローダーは相手名と配置オプションを読み捨て、片側だけを通常の Animation として `Shared` カテゴリに載せている（`RpEmotesLoader.Parse` は `arr[3]` を使っていない）
- どちらの側にも配置情報が無いペアが rpemotes に 16 件ある（`baseball` / `baseballthrow`、`punch` / `punched`、`slap` 系、`stickup` / `stickupscared`、`give` / `give2`、`headbutt` 系、`holdmee`、`ccat`）。ゲーム内でもプレーヤーの立ち位置のまま再生されるので、プレビューでは既定配置（1 m で向き合う）にする
- 動物とのペア（`csdog` 系・`cbdog` 系・`ccat` 系、`AnimalEmote` 付き 8 件）は相手が動物 ped なので M8 が前提
- タイミングの差: `StartDelay`（rpemotes 2 件）/ `Delay`（scully 2 件）、片方だけループするペアが少数

タスク:

1. カタログ: `EmoteEntry` に `PartnerCommand` / `PartnerId`（相手のコマンド。同ソース・同カテゴリ内で解決して id にする）と `Placement`（`SharedPlacement`: `Offset(side, front, height, heading)` または `Attach(bone, [x,y,z,rx,ry,rz])`）、`StartDelayMs` を追加し、両ローダーで読む。scully の `OtherEmote` も同様。相手が見つからない・相手のクリップが無い場合は DTO の `partner` / `partner.previewReason` で伝える（主側の `previewable` は変えない）
2. API: `EmoteDto` に `partnerCommand` / `startDelayMs` / `partner`（id、辞書、クリップ、主から見た配置、開始遅延、ループ、小道具、プレビュー可否、動物 ped）を追加。クリップは既存の `/api/emotes/{id}/clip?ped=` で取れるので新設しない
3. Web: ビューアの「スケルトン＋マネキン＋小道具＋ミキサー」を `RigSlot` として切り出し、主・相手の 2 スロットを持つ。配置は Offset なら主 ped の座標系で相手の実体を置いて回す、Attach ならぶら下がる側の実体を相手のボーンの子にする（小道具と同じ `YZX`）。配置情報の無いペアは既定配置。時間軸は 1 本（`Timeline`）で開始遅延を反映し、短い方はループ指定なら巻き戻し、無ければ最終姿勢で保持
4. UI: 詳細パネルに相手エモートへのリンク・配置の説明・開始遅延、HUD に「相手」トグル（設定 `viewer.showPartner`、既定 on）と相手 ped のボタン。相手側を選んだときも同じペアが表示される（主・相手を入れ替えるだけ）。相手 ped は設定 `partnerPed` で選べる（既定は主と同じ）
5. DevTools: `shared` コマンドで全ペアの解決状況（相手あり／なし、配置方式、クリップ有無）を一覧する
6. テスト: 両ローダーの相手・配置の読み取り（fixture に `hug` / `hug2`、`carry` / `carry2`、scully の `sbro` / `sbro2` を追加）、相手 id の解決、`EmoteDto.partner` の出力

完了条件: `hug` を選ぶと向き合った 2 体が同時にハグし、`carry2`（Be carried）を選ぶと主 ped（抱えられる側）が相手の `SKEL_R_Clavicle`（40269）にぶら下がった状態で再生される（`carry` を選べば相手がぶら下がる）。scully の `sbro` でも同様。配置情報の無い `punch` は既定配置で 2 体が出る。

結果（2026-09-06）: 上記をすべて実機で確認。加えて `cprs`（開始遅延 0.25 秒＋エンティティ原点への貼り付け）、`csdog`（人間がパグを抱える）、`hug2`（相手側を選ぶと主・相手が入れ替わる）、「相手」トグルの ON/OFF、手動クリップ選択で相手が消えることを確認。実装上の判断は冒頭の「M9 で決めたこと」を参照。

## M10: エモートの重ね合わせ（2 スロット再生）

目標: ゲームが同時に再生する 2 本のエモート（プライマリ = 全身、セカンダリ = flag に SECONDARY(32) が立つもので、UPPERBODY(16) なら上半身だけ）を、ゲームと同じ規則で重ねて見られる。

前提の調査（2026-09-11、private の `Docs/エモート重ね合わせ調査/調査メモ.md`）: 実機に既知角度の診断クリップ（骨盤・ROOT・前傾・T ポーズ・腕だけ・脚と腕の時計）を入れ、規則を確定した。スロットは 2 つで同スロットは後勝ち、時間軸はスロットごとに独立、ROOT の位置と骨盤・脚はプライマリ、マスク内でセカンダリがトラックを持つボーンはセカンダリ、ROOT の回転だけ二重に持つ（`Spine_Root` を セカンダリの ROOT 回転の下に付け替える）、境界の重みは 0/1、開始時のブレンドは 1 / BlendInSpeed 秒。

タスク:

1. Core: `EmoteEntry.AnimFlag` を両ローダーで読む（rpemotes `Flag` → `onFootFlag` → 旧ブール、scully `Stuck / Move / Loop` の優先順）。`Loop` / `Move` / `UpperBody` は flag のビット。DTO に `flag` / `slot` / `upperBody`
2. App: `/api/clipsets/{set}/{clip}` で移動クリップセットのクリップを焼く（ニュートラルの `idle` 用）
3. Web: `Playback` を 2 レイヤー化（トラックを分けた 2 つの `AnimationAction`、上半身マスク、ROOT の付け替え、独立クロック）。左ペインを上段＋下段（独自の検索とソース・カテゴリ・種別の選択、ドラッグ分割）に。合成オン中は上段がプライマリだけ、下段がセカンダリだけ。flag がセカンダリのエモートは単独でも上半身だけ（脚は歩き方の idle。実機で確認）。詳細パネルに解除、Esc。当初の「下段は反対スロットに自動絞り込み、詳細でスロット上書き」は分かりにくいと指摘され、同日に「合成中はリストをスロットで分ける」に落ち着いた
4. テスト: flag の読み取り（Core）、DTO のスロット、`/api/clipsets` の 503（App）
5. 検証: 診断リソース `tools/diag/preview-res`（private）を読ませて `tools/shot-layer.mjs` で撮り、実機画像と照合

結果（2026-09-11）: 照合ケース 8 件（脚だけ倒れる、胴体だけ腰から倒れる、前傾が直立に戻る、前傾のまま腕だけ、脚・骨盤は動かない、脚 4 秒・腕 3 秒で独立に回る、pushup + crossarms、sit + clap）がすべて実機画像と一致。実装中の落とし穴: three.js の `PropertyMixer` は評価値が変わらないボーンを書き戻さないため、ROOT の付け替えをボーンの現在値に掛けると同じ時刻を再表示するたびに回転が重なる（胴体が背中側へ折れて見えた）。付け替えは焼き込みデータから毎回計算する。未対応: セカンダリの開始オフセット、開始時のブレンド（即時切替）、車内 flag 35、動物 ped のニュートラル idle。

## M11: 旧 rpemotes と dpemotes への対応

目標: rpemotes-reborn の前身である rpemotes（`Daudeuf/rpemotes`、TayMcKenzieNZ 版の 2024-02 時点のスナップショット、325 本の同梱 `.ycd` と 153 本の小道具）と、さらにその前身の dpemotes（`andristum/dpemotes`、カジノ DLC のアニメーションを同梱）を、そのままフォルダ指定または GitHub 取得で読める。古いメニューを使い続けているサーバーの一覧を確認したい利用者向け。

事前調査（2026-09-12）: 両方をクローンして既存の `RpEmotesLoader` に通したところ、書式は rpemotes-reborn とほぼ同じで、差分は 4 点だけだった。

1. `types.lua` が無い（両方）。種別判定が `types.lua` を必須にしていたので検出できない。リストは旧式の `EmoteLoop / EmoteMoving / EmoteStuck` ブールしか使わず、`ReadFlag` は既にこの経路を持つ（両方の `Emote.lua` も Moving → 51、Loop → 1、Stuck → 50 で reborn と同じ）
2. `PtfxInfo = Config.Languages[Config.MenuLanguage]['pee']` のように、リストがリソースの `config.lua` のグローバル `Config` を参照する（rpemotes 10 箇所、dpemotes 4 箇所。reborn には無い）。無いと Lua の実行時エラーで読み込み全体が失敗する
3. dpemotes はグローバルが `DP`、フォルダが `Client/`（大文字）
4. dpemotes の Expressions は `{"Expression", "mood_angry_1"}` 形式（名前が 2 番目）

それ以外（Walks、Shared の 4 番目、Scenario / MaleScenario / ScenarioObject、Prop / SecondProp、SyncOffset*、Attachto、EmoteDuration、StartDelay、AnimalEmote、末尾でその場マージする `AnimationListCustom.lua`）はそのまま通る。内容は reborn と大部分が重なる（rpemotes だけのクリップ 45、dpemotes だけのクリップ 47）。

タスク:

1. Core: `ResourceSource.DetectKind` は `client/AnimationList.lua`（大文字小文字を無視）だけで rpemotes 系と判定し、ファイル先頭が `DP = {` なら `DpEmotes`。`types.lua` は任意。`KindName` で API / UI 向けの名前（`rpemotes` / `dpemotes` / `scully`）を一元化
2. Core: サンドボックスに `AnimFlag` / `ScenarioType` の既定値と、何を引いても自分を返す `Config` スタブを置く。`RpEmotesLoader` は `RP` が無ければ `DP` を読み、Expressions の dpemotes 形式を吸収
3. App: 取得テンプレートに `Daudeuf/rpemotes@master` と `andristum/dpemotes@master` を追加。種別の文字列を `KindName` に寄せる
4. Web: 種別 `dpemotes` の型と表示名
5. テスト: 種別判定（types.lua 無し / `Client/` + `DP`）、旧 rpemotes の `Config` 参照とその場マージ、dpemotes の Expressions / Walks / Shared / PropEmotes（Core 80 件、App 17 件）

結果（2026-09-12）: 実物のクローンを読ませて rpemotes 1,645 件（再生可 1,522。残りは表情・シナリオ 69、リソース側の誤記 `nill` などクリップ無し 47、辞書無し 7）、dpemotes 529 件（再生可 450。表情・シナリオ 66、クリップ無し 8、辞書無し 5）、警告 0。同梱 `.ycd` からの再生（`dancesilly7`）と Shared の相手解決（`hug` → `hug2`、前方 1.05 m）をヘッドレス画面で確認。dpemotes の `Emote.lua` にはネストした `if` の取り違えがあるが、リストの読み取りには関係しない。

## 横断事項

- **テスト**: Core は xunit で継続し、`ClipBaker`、複数ソースのカタログ、`ResourceSource` の判定、`.bin` レイアウトを追加する。App は `WebApplicationFactory` による API テスト。Web は v1 では自動テストを持たず、DevTools の `coverage` と目視で担保する。UI の確認はヘッドレス Chrome で描画して見る（FxDeck と同じやり方）
- **検証時の約束**: 必ず `--data-dir` に一時フォルダを指定し、本物の `%LOCALAPPDATA%\EmotePreviewer` を触らない。exe の差し替えは起動中だと失敗する
- **依存関係**: NuGet は `Microsoft.AspNetCore.App`（FrameworkReference）と `NLua`。npm は `react` / `react-dom` / `zustand` / `three` / `@types/three` / `vite` / `typescript`。gta-toolkit は `third_party/` のまま。バージョン更新は明示的に行う
- **言語**: ドキュメントは日本語（README は英語版を正にし日本語版を並置、M6 で分離）。識別子・コメント・ログ・コミットメッセージは英語。UI 文字列は辞書に集約し、実行コードに日本語リテラルを置かない
- **ゲームデータの扱い**: アプリは GTA のファイルを読むだけで書き出さない。`cache/` に置くのは索引とスケルトンの姿勢のみ。API は `127.0.0.1` からしか呼べない

## 未決事項

判断済み:

- 索引キャッシュ: 入れない（索引 1.1〜1.4 秒）
- テクスチャ表示: M7 で入れた（ディフューズのみ。当初は見送る判断だった）
- トレイ常駐: しない（コンソール + ブラウザで足りる）
- ログドロワー: 付けない（コンソールと `logs/` で足りる）

残り:

- Walks の走り（`run`）や向き別クリップは扱わない。必要になったら `ClipSetTable` にクリップ名を渡すだけで解決できる
