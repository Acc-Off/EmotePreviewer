# RAGE クリップ辞書（.ycd）アニメーションデータ仕様

対象: GTA V PC 版（Legacy）の `.ycd`（`crClipDictionary`）に含まれる **アニメーションのキーフレームデータ（Sequence ブロック）の復号と評価**。

この文書は、ゲームデータの観察と解析から書き起こしたフォーマットの記述で、`src/EmotePreviewer.Core/Rage/Anim/` のデコーダはこの文書に基づいて実装されている。挙動の検証には、`poc regression` で自分の GTA V から生成する回帰フィクスチャ（`tests/fixtures/`）を使う。

用語:

- **リソース**: RSC7 コンテナに入った RAGE のページ化バイナリ。ポインタ解決（system/graphics セグメント）は本仕様の範囲外で、gta-toolkit（MIT）の `ResourceDataReader` が担う。
- **アニメーション（Animation）**: 1 本のアニメーション。複数の **トラック** を持ち、時間軸を **シーケンスブロック** に分割して格納する。
- **トラック（track）**: 「どのボーンの、どの量（位置・回転・スケール等）か」の単位。
- **チャネル（channel）**: トラックを構成する成分ごとの値列（例: 位置 = X, Y, Z の 3 チャネル）。
- **フレーム**: 離散サンプル。通常 30 fps。

数値はすべてリトルエンディアン。`u8/u16/u32` は符号なし整数、`i32` は符号付き、`f32` は IEEE754 単精度。

---

## 1. 全体構造（gta-toolkit が既に読める部分）

```
ClipDictionary
 ├─ Clips : ハッシュマップ<u32, Clip>          … クリップ名の Jenkins one-at-a-time ハッシュ → Clip
 └─ Animations : ハッシュマップ<u32, Animation> … アニメーション名ハッシュ → Animation

Clip（Type バイトで種類が決まる）
 ├─ Type 1 = ClipAnimation  : Animation を 1 本参照。StartTime, EndTime, Rate を持つ
 └─ Type 2 = ClipAnimations : Animation の配列（各要素に StartTime, EndTime, Rate）と全体 Duration

Animation
 ├─ ヘッダ（96 バイト、下記 2 章）
 ├─ Sequences : Sequence ブロックへのポインタ配列
 └─ Tracks    : トラック定義の配列（4 バイト × N）
```

- クリップ名ハッシュは **クリップ名（拡張子や辞書名を含まない短い名前）をそのまま** Jenkins one-at-a-time にかけたもの。観測した 574 件すべてでファイル内のハッシュ値と一致した。名前は小文字で格納されている。
- gta-toolkit では `Clip.Name` が `string_r`、`Animation` のヘッダ項目は `Unknown_10h` 等の名前で読まれている。本仕様の 2 章の表で対応を示す。

## 2. Animation ヘッダ（96 バイト）

| オフセット | 型 | 意味 | gta-toolkit の名前 |
|---|---|---|---|
| 0x00 | u32 | VFT（無視） | – |
| 0x04 | u32 | 常に 1 | – |
| 0x08 | u32 | 0 | – |
| 0x0C | u32 | 0 | – |
| 0x10 | u8 | フラグ（用途不明。観測値 0/1/8/16/24。復号には不要） | `Unknown_10h` の下位バイト |
| 0x11 | u8 | 常に 1 | `Unknown_10h` の上位バイト |
| 0x12 | u16 | 0 | `Unknown_12h` |
| 0x14 | u16 | **Frames**: フレーム数 F | `Unknown_14h` |
| 0x16 | u16 | **SequenceFrameLimit**: シーケンスブロック 1 個が担当するフレーム数 L | `Unknown_16h` |
| 0x18 | f32 | **Duration**: 長さ（秒） | `Unknown_18h` |
| 0x1C | u32 | 署名らしきハッシュ（無視） | `Unknown_1Ch` |
| 0x20–0x37 | u32×6 | 0 | – |
| 0x38 | u32 | MaxSeqBlockLength: 最大シーケンスブロック長（検証用。無視可） | `Unknown_38h` |
| 0x3C | u32 | UsageCount（無視） | `Unknown_3Ch` |
| 0x40 | ptr list | Sequences: ポインタ u64 + 要素数 u16 + 容量 u16 + パディング u32 | `Sequences` |
| 0x50 | list | Tracks: ポインタ u64 + 要素数 u16 + 容量 u16 + パディング u32 | `Tracks` |

- 通常 `(F − 1) / Duration ≈ 30`。
- `F = 1` や `Duration = 0` のアニメーションが実在する（静止ポーズ）。

### 2.1 トラック定義（4 バイト）

| オフセット | 型 | 意味 |
|---|---|---|
| 0 | u16 | **BoneId**: ボーンタグ（スケルトンの `Bone.BoneId`/`Tag` と突合する）。ルートは 0 |
| 2 | u8 | **Format**: 値の型。0 = Vector3、1 = Quaternion、2 = Float |
| 3 | u8 | **TrackId**: 量の種類（下表） |

観測した TrackId と Format の対応（Format はこの表と完全に一致した）:

| TrackId | 意味 | Format |
|---|---|---|
| 0 | ボーン位置（親相対） | 0 Vector3 |
| 1 | ボーン回転（親相対） | 1 Quaternion |
| 2 | ボーンスケール | 0 Vector3 |
| 5 | ルートモーション位置 | 0 Vector3 |
| 6 | ルートモーション回転 | 1 Quaternion |
| 7 / 8 | カメラ位置 / 回転 | 0 / 1 |
| 22, 24, 33, 53, 134, 136–140 | 表情・その他の単一値 | 2 Float |
| 25 | 表情（オイラー角） | 0 Vector3 |
| 26 | 表情（回転） | 1 Quaternion |

ボディプレビューで必要なのは 0, 1, 2（と任意で 5, 6）。他は復号はできるが用途は本仕様の範囲外。

**トラックの index**（Tracks 配列内の位置）が、各シーケンスブロック内でチャネルをトラックに結びつける鍵になる（4.3 参照）。

## 3. Sequence ブロック

アニメーションはフレーム軸を L フレームごとのブロックに分割する。ブロック数 = `ceil((F − 1) / L)`（F = 1 のときは 1）。ブロック i は論理フレーム `i·L` から `i·L + L` までの **L + 1 フレーム** を保持する（末尾の 1 フレームは次ブロック先頭と同じ内容で、補間用に重複している）。最終ブロックは残りフレーム数 + 1 だが、**例外として少ないものがある**（観測: 期待 75 に対し 74、期待 120 に対し 117 の 2 例）。ブロック内フレーム数は必ずヘッダの NumFrames を信じること。

### 3.1 ヘッダ（32 バイト）

| オフセット | 型 | 意味 | gta-toolkit の名前 |
|---|---|---|---|
| 0x00 | u32 | 識別ハッシュ（無視） | `Unknown_0h` |
| 0x04 | u32 | **DataLength**: 続くデータ部のバイト数 | `DataLength` |
| 0x08 | u32 | 0 | `Unknown_8h` |
| 0x0C | u32 | **FrameOffset**: データ部先頭からフレームレコード領域までのバイトオフセット | `Unknown_Ch` |
| 0x10 | u32 | **RootMotionRefsOffset**: ブロック先頭（ヘッダ含む）からルートモーション参照表までのオフセット。参照が無いとき `DataLength + 32`（= ブロック長） | `Unknown_10h` |
| 0x14 | u16 | 0 | `Unknown_14h` の下位 16 bit |
| 0x16 | u16 | **NumFrames**: このブロックのフレーム数 | `Unknown_14h` の上位 16 bit |
| 0x18 | u16 | **FrameLength**: フレームレコード 1 個のバイト数（0 もある） | `Unknown_18h` の下位 16 bit |
| 0x1A | u16 | IndirectQuantizeFloat の値表が占める 32 bit ワード総数（検証用） | `Unknown_18h` の上位 16 bit |
| 0x1C | u16 | QuantizeFloat のフレームあたりビット総数（検証用） | `Unknown_1Ch` |
| 0x1E | u8 | **ChunkSize**: LinearFloat のチャンク幅（観測値 64 または 255） | `Unknown_1Eh` の下位 8 bit |
| 0x1F | u8 | **RootMotionRefCounts**: 上位 4 bit = 位置参照数、下位 4 bit = 回転参照数 | `Unknown_1Eh` の上位 8 bit |
| 0x20 | u8[DataLength] | **Data** | `Data` |

### 3.2 Data の配置

Data 内のオフセットはすべて Data 先頭基準。

```
[0, FrameOffset)                          チャネルパラメータ領域（4.2 の順に詰めて格納）
[FrameOffset, FrameOffset + FrameLength × NumFrames)
                                          フレームレコード領域: NumFrames 個の固定長レコード
ChannelListOffset = FrameOffset + FrameLength × NumFrames
  [ChannelListOffset, +18)                チャネル数表: 9 種類のチャネル型ごとに u16（型 0 → 8 の順）
ChannelDescOffset = ChannelListOffset + 18
  [ChannelDescOffset, ...)                チャネル記述子: 型ごとに count 個の u16、型ごとに 4 個単位へパディング
[RootMotionRefsOffset − 32, +6 × (位置参照数 + 回転参照数))
                                          ルートモーション参照表（位置参照が先、回転参照が後）
```

チャネル記述子のパディング: ある型の記述子を count 個読んだ後、`count mod 4 ≠ 0` なら `(4 − count mod 4) × 2` バイトを読み飛ばす（記述子を 4 個 = 8 バイト単位に揃える）。count = 0 のときは何も読み飛ばさない。

ルートモーション参照（6 バイト）: `u8 channelType, u8 channelIndex, u16 dataIntOffset, u16 frameBitOffset`。評価には使わない（ルートモーションはトラック 5/6 として普通に評価できる）。参照表の位置は必ず `RootMotionRefsOffset − 32`（Data 基準）で求める。Rockstar 製ファイルではこれが記述子領域の直後と一致し、参照表の末尾が Data の末尾と一致する。コミュニティ製ファイルの一部では記述子領域と参照表の間に 1.5KB 前後の詰め物があり、参照数（RootMotionRefCounts の 2 つのニブル）は 8〜15 個といった値になるが、`RootMotionRefsOffset` から数えると表のサイズは Data 末尾までぴったり収まる（手元の 547 ブロックで確認済み）。

## 4. チャネル

### 4.1 チャネル型

| 型 | 名前 | パラメータ（4.2） | フレームあたりビット数（4.4） | 成分数 |
|---|---|---|---|---|
| 0 | StaticQuaternion | f32 × 3 | 0 | 4 |
| 1 | StaticVector3 | f32 × 3 | 0 | 3 |
| 2 | StaticFloat | f32 | 0 | 1 |
| 3 | RawFloat | なし | 32 | 1 |
| 4 | QuantizeFloat | i32 ValueBits, f32 Quantum, f32 Offset | ValueBits | 1 |
| 5 | IndirectQuantizeFloat | i32 FrameBits, i32 ValueBits, i32 NumInts, f32 Quantum, f32 Offset, 値表 | FrameBits | 1 |
| 6 | LinearFloat | i32 NumInts, i32 Counts, f32 Quantum, f32 Offset, チャンクデータ | 0 | 1 |
| 7 | CachedQuaternion1 | なし | 0 | （回転の欠損成分を再構成） |
| 8 | CachedQuaternion2 | なし | 0 | （効果なし） |

型 3（RawFloat）はエモート用クリップ約 700 本の走査で 1 度も現れなかった。実装はあるが実データでは未検証。

### 4.2 読み出し順序（重要）

チャネル数表・記述子・パラメータ領域は **同じ順序で対応** する。すなわち:

```
paramPos = 0
for type in 0..8:
    count = チャネル数表[type]
    for k in 0..count-1:
        desc = 記述子（u16）を読む
        このチャネルのパラメータを paramPos から読み、paramPos を進める
    記述子のパディング
```

パラメータの消費バイト数:

- 型 0, 1: 12 バイト。型 2: 4 バイト。型 3, 7, 8: 0 バイト。
- 型 4: 12 バイト。
- 型 5: 20 バイトのヘッダに続いて **NumInts × 4 バイト** の値表。値表は `paramPos`（ヘッダ直後）のバイト境界から始まるビット列（4.5 のビット順）で、要素数 `numValues = min( floor(NumInts × 32 / ValueBits), 2^FrameBits − 1 )`、各要素 ValueBits ビット。要素 j の実数値 = `bits × Quantum + Offset`。表を読んだら `paramPos` をヘッダ直後 + NumInts × 4 に進める（余りビットは無視）。
- 型 6: **NumInts × 4 バイト（16 バイトのヘッダを含む）**。つまり `paramPos` はチャネル先頭 + NumInts × 4 に進む。中身は 4.6。

### 4.3 記述子（u16）

- `desc >> 2` = **トラック index**（2.1 の Tracks 配列の位置）
- `desc & 3` = **スロット**（トラック内の成分位置 0–3）
- 型 7 / 8 だけは `desc & 3` を **QuatIndex**（再構成する四元数成分の位置 0–3）と解釈し、スロットは型 7 なら 3、型 8 なら 4 に固定する。

同じトラック index を持つチャネルを集め、スロット順に並べたものがそのトラックの **チャネル配列** になる。観測上、スロットは常に 0 から連続しており、全トラックが全ブロックに現れる。万一トラックのチャネルが 1 つも無いブロックがあれば、そのトラックは 0（四元数なら単位四元数）を返す。

観測されたチャネル配列の代表例（トラック Format 別）:

| Format | チャネル型の並び | 意味 |
|---|---|---|
| 0 Vector3 | (1) | 静的ベクトル |
| 0 Vector3 | (4,4,4) / (5,5,5) / (6,6,6) / (5,5,2) など | X, Y, Z を各 1 チャネル |
| 1 Quaternion | (0) | 静的四元数 |
| 1 Quaternion | (4,4,4,7) / (6,6,6,7) / (2,2,4,7) など | 3 成分 + 型 7 で 4 成分目を再構成 |
| 1 Quaternion | (4,4,4,4) / (4,4,4,2) | 4 成分をそのまま格納 |
| 1 Quaternion | (6,6,6,6,8) / (4,4,4,4,8) など | 4 成分をそのまま格納 + 型 8（無視） |
| 2 Float | (2) / (4) / (5) / (6) | 1 成分 |

### 4.4 フレームレコード

フレーム f（ブロック内ローカル、0 ≤ f < NumFrames）のレコードは Data の `FrameOffset + FrameLength × f` から FrameLength バイト。レコード内は 1 本のビット列（4.5）で、**4.2 と同じ順序**（型 0→8、型内はチャネル順）に各チャネルが自分の「フレームあたりビット数」を消費する。ビット数 0 のチャネルは何も消費しない。

- 型 3: 32 bit をそのまま IEEE754 単精度と解釈する。
- 型 4: ValueBits ビットの整数 q → 値 `q × Quantum + Offset`。ValueBits = 0 なら値は Offset。
- 型 5: FrameBits ビットの整数 j → 値表[j]（値表は 4.2）。

### 4.5 ビット列の規約

ビット列内のビット番号 b は、バイト `floor(b / 8)` の **ビット `b mod 8`（0 = 最下位）** を指す。n ビットの値を読むときは、先に読んだビットが値の下位側になる（LSB ファースト）。ビット列がデータ末尾を越えた場合は 0 として読む。

### 4.6 LinearFloat（型 6）

チャンク単位の差分符号化。ヘッダの `Counts` を 3 つのビット幅に分ける:

- `C1 = Counts & 0xFF`: チャンクごとの **差分ビット列オフセット** のビット幅
- `C2 = (Counts >> 8) & 0xFF`: チャンクごとの **先頭値** のビット幅
- `C3 = (Counts >> 16) & 0xFF`: フレームごとの **差分の下位ビット** のビット幅

`numChunks = ceil(NumFrames / ChunkSize)`。ビット列はヘッダ直後のバイト境界 `startBit = (チャネル先頭 + 16) × 8` から始まり、次の順に並ぶ:

1. `chunkOffset[i]`（C1 ビット）× numChunks。C1 = 0 なら全て 0。
2. `chunkValue[i]`（C2 ビット）× numChunks。C2 = 0 なら全て 0。
3. 差分領域。基点 `deltaBase = startBit + numChunks × (C1 + C2)`。

チャンク i の復号:

```
bitpos = deltaBase + chunkOffset[i]
value  = chunkValue[i]            （量子化整数。符号付きとして扱う）
inc    = 0
for j in 0 .. ChunkSize-1:
    frame = i × ChunkSize + j
    if frame >= NumFrames: break
    出力[frame] = value × Quantum + Offset
    if j + 1 >= ChunkSize: break          （チャンク最終フレームの後には差分が無い）
    delta = C3 ビット読む（C3 = 0 なら 0）
    k = 次に 1 のビットが現れるまでに読んだ 0 のビット数（1 のビット自体も消費する）
        ※ ビット列の末尾に達したら打ち切る
    delta = delta | (k << C3)
    if delta != 0:
        sign = 1 ビット読む。1 なら delta = -delta
    inc   = inc + delta
    value = value + inc
```

つまり値は「差分の差分」（加速度）で符号化されており、各フレームの差分 `inc` は前フレームの `inc` に `delta` を足して更新する。`delta` の上位部分はゼロランレングス（Elias-γ 風）で表される。

このチャネルはフレームレコードのビットを消費しない（フレームあたり 0 ビット）。

### 4.7 CachedQuaternion（型 7 / 8）

- 型 7: そのトラックの **スロット 0, 1, 2** の 3 チャネルを (a, b, c) とし、`n = sqrt(max(1 − (a² + b² + c²), 0))` を **QuatIndex の位置に挿入** して四元数 (x, y, z, w) を作る。
  - QuatIndex 0 → (n, a, b, c)、1 → (a, n, b, c)、2 → (a, b, n, c)、3 → (a, b, c, n)
- 型 8: 4 成分が全て格納されているトラックに付随して現れる（スロット 4）。評価には影響しない。
- 型 0（StaticQuaternion）は (x, y, z) を格納し、`w = sqrt(max(1 − (x² + y² + z²), 0))`。

## 5. 評価

### 5.1 ブロック内のトラック値（フレーム単位）

トラック index i、ブロック内ローカルフレーム f（0 ≤ f ≤ NumFrames。f = NumFrames は次の項の f1 として到達し得る）に対し:

- 各チャネルのフレーム f の値: 静的型は定数、型 4/5/3 はフレームレコードから、型 6 は復号済み配列から。**配列添字は `f mod 配列長`** とする（f = NumFrames のとき先頭に巻き戻る。既存ツール群と互換の解釈で、3 章の例外ブロックでのみ意味を持つ）。
- **四元数トラック**（Format 1）:
  - 型 7 チャネルがある → 4.7 の再構成。
  - 無い → スロット順に成分を詰める（4.7 末尾の型 0 は 4 成分、それ以外は各 1 成分）。4 成分に満たなければ残りは 0。型 8 は無視。
  - **正規化はしない。**
- **ベクトル／実数トラック**（Format 0, 2）: スロット順に成分を詰める（型 1 は 3 成分、型 0 は 4 成分、他は 1 成分。4 成分を超えた分は捨てる）。4 成分に満たなければ残りは 0。結果は (x, y, z, w) の 4 成分として扱い、Vector3 は先頭 3 成分、Float は先頭 1 成分。

### 5.2 アニメーション時刻 → フレーム位置

アニメーション内時刻 t（秒、0 ≤ t）に対し:

```
nframes = F − 1
curPos  = (t / Duration) × nframes
Frame0  = floor(curPos)   （u16 に切り捨て）  mod F
Alpha1  = curPos − floor(curPos)
Alpha0  = 1 − Alpha1
```

- `F ≤ 1` または `Duration ≤ 0` の場合は Frame0 = 0, Alpha1 = 0 とする（そのまま計算すると 0 除算で NaN になる）。
- ブロック番号 `s = Frame0 div L`、ローカルフレーム `f0 = Frame0 mod L`、`f1 = f0 + 1`。Frame0 の有効範囲は 0 〜 F − 2（F − 1 は f1 としてのみ現れる）。t ∈ [0, Duration) なら範囲内に収まる。

### 5.3 補間

- ベクトル／実数: `v = v(f0) × Alpha0 + v(f1) × Alpha1`（成分ごとの線形補間）。
- 四元数: `q = normalize( q(f0) × (1 − Alpha1) + q(f1) × Alpha1 )`。成分ごとの線形補間を正規化する（nlerp）。**符号合わせ（内積が負なら反転）は行わない**。既存ツール群と互換の解釈。
- 補間しない評価（`interpolate = false`）は f0 の値をそのまま返す（四元数は正規化しない）。

### 5.4 クリップ時刻 → アニメーション時刻

- **ClipAnimation**（Type 1）: `scaled = t × Rate`、`dur = EndTime − StartTime`、`animTime = StartTime + (scaled mod dur)`。再生長は `dur / Rate` 秒。
- **ClipAnimations**（Type 2）: まず `tc = t mod Duration`（クリップ全体の Duration。0 なら最長要素の `(EndTime − StartTime) / Rate`）を取り、**各要素ごとに** Type 1 と同じ式 `animTime = StartTime + ((tc × Rate) mod (EndTime − StartTime))` で自分のアニメーションの時刻に写す。要素は順に評価し、同じトラックがあれば後の要素が上書きする。ルートモーション（トラック 5/6）は要素間で加算／合成する。
  - 2026-09-11 訂正: 以前は「各要素の StartTime / Rate は使わず全要素に `tc` をそのまま与える」としていたが誤り。Type 2 の要素は長いシーン用アニメーションの一部区間（例: `friends@frj@ig_1` の `wave_a` は 67.9 秒のアニメーションの 53.2〜56.7 秒と 111.7 秒のアニメーションの 37.6〜41.1 秒）を指すことが多く、先頭から再生すると別の動きになる（手を振るはずが腕が上がらない）。

### 5.5 ルートモーションと補助ボーン

- トラック 5 の値は各要素分を加算、トラック 6 は四元数を順に合成する。ボーン変換への適用はデコーダの範囲外（`PoseSolver`）。
- RB_L_ThighRoll（タグ 23639）/ RB_R_ThighRoll（6442）は多くのクリップでアニメーションされないため、SKEL_L_Thigh（58271）/ SKEL_R_Thigh（51826）の回転を複写して表示する。これは表示上の補正で、デコーダの範囲外（`PoseSolver` が担当）。

## 6. 数値精度

- 量子化値は `bits × Quantum + Offset` を単精度で計算する（`bits` は整数、Quantum/Offset は f32）。
- 回帰フィクスチャとの比較は絶対誤差 1e-4 を許容する（単精度の演算順序の差を吸収するため）。

## 7. 既知の落とし穴

- ブロック内 NumFrames は `L + 1` とは限らない（3 章）。
- 記述子領域と Data 末尾の間に詰め物があるカスタムファイルがある。ルートモーション参照表は `RootMotionRefsOffset` で探す（3.2）。
- `Duration = 0` / `F = 1` のアニメーション（5.2）。
- 型 8 は「4 成分格納」の目印であって、復号すべきデータを持たない（4.7）。
- LinearFloat のゼロラン探索はビット列末尾で必ず打ち切る（4.6）。
- ヘッダ無し（RSC7 ヘッダのない生リソース）の `.ycd` がコミュニティ製アニメに混ざる。これはコンテナ層の話で、`Data` の中身は同じ。

## 8. 実装上の注記

本文の記述だけでは決まらず、実装（`src/EmotePreviewer.Core/Rage/Anim/`）で解釈を決めた箇所。

- **4.6 LinearFloat の `chunkValue`**: C2 ビットの値を符号なしで読み、`int` に入れて以降の加算（`inc` の加減）を符号付きで行う。符号拡張はしない。
- **4.6 ゼロラン探索の「ビット列の末尾」**: チャネル先頭 + `NumInts × 4` バイト（そのチャネルのデータ末尾）とした。Data 末尾ではない。
- **4.2 IndirectQuantizeFloat の `ValueBits = 0`**: 0 除算になるので、`numValues = 2^FrameBits − 1` とし、各要素は `Offset`。実データでは未観測。
- **4.4 フレームレコードの読み出し範囲**: レコード境界ではなく Data 全体を対象にビット列を読む（整形式データでは同じ結果）。データ末尾を越えた分は 0。
- **5.1 `f = NumFrames` の巻き戻し**: フレームレコードと LinearFloat 配列の両方で `f mod NumFrames` を適用。
- **5.2 `Frame0` の計算**: `curPos = (t / Duration) × (F − 1)` を単精度で計算し、`floor` → `ushort` → `mod F`。
- **5.4 再生長のキャップ（デコーダ外、アダプタの判断）**: `ClipAnimation` の `IClip.Duration` は `min(EndTime − StartTime, Animation.Duration) / Rate`。コミュニティ製クリップは EndTime がアニメより 1 フレーム長く（90 フレーム 3.708 s に対し 3.75 s）、そのままだと末尾で先頭フレームへ巻き戻るため。
- **量子化値の演算**: `(float)bits * Quantum + Offset` を単精度で計算。許容誤差 1e-4 で全一致。
- **性能**: 最大のアニメ（1,021 フレーム × 153 トラック、735 KB）で、全ブロックのチャネル構築 + LinearFloat 復号が 0.1 ms、全フレーム × 全トラックの評価（フレームレコードは遅延読み出し）が 35 ms（Release）。
