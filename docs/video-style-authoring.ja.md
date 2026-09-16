# Aibos Image 動画スタイルJSON・指示言語ガイド

確認日: 2026-09-16。実装基準: この資料と同じリビジョンの `VideoPromptProgram.cs`、`VideoDirectionTimeline.cs`、`VideoPromptEnhancement.cs`。
対象: ネイティブWPF版 Aibos Image、MiniMax H3向け動画スタイル、保存形式 Version 1。

AI補完の新しい動作・音声設定と旧ジョブとの互換性は、[動画のAI補完](video-prompt-enrichment.ja.md)を参照してください。以下のスタイル保存形式とは別の、生成ジョブ側の版2契約です。

この資料は、別のAIや編集者が会話履歴なしでスタイルを理解・編集するための、単独で読めるリファレンスです。例文は架空の通常動作です。既存スタイルの本文や個人の設定は含みません。同じリビジョンの実装を説明し、旧版や将来版の動作は保証しません。

## 1. 最初に理解すること

スタイルは「生成設定」「生成する内容」「操作できる選択肢」「日本語の説明」を一緒に保存したものです。Aibosの指示言語をそのまま動画モデルへ渡すわけではありません。

```text
一つのスタイルを選ぶ
  → 今回の画像をアニメ／実写のどちらとして扱うか決める
  → 本文中の色付き部分をクリックして、使う文や候補を選ぶ
  → 必要なら冒頭の動き・腕・表情・ムードを選ぶ
  → キューに追加
      AI強化なし: 手動選択と既定値をローカルで解決した本文で生成
      AI強化あり: 順番が来た直前に画像と保存済み指示から強化し、生成
```

**AI強化を待ってからキューへ登録する仕様ではありません。** 下部の「AIでプロンプトを強化」は初期状態では未選択です。チェックなしでも登録できます。ONの場合も先に登録し、そのジョブがFIFO順で取得された後、動画生成の直前に強化します。

| 用語 | 意味 |
|---|---|
| スタイル | `VideoStyles` 配列の1要素。旧TemplateとStyleは同じ選択場所で扱う |
| `InstructionProgram` | Aibosの編集用データ。本文、選択状態、条件、説明、共通演技設定を持つ |
| `Template` | `[]`・`{}`を含められる編集用本文 |
| H3本文 | Aibosの選択記号を解決した、MiniMax H3へ渡す文章 |
| ベース本文 | `BaseH3Template`。注釈を加える前の完全なH3本文、または追記先のH3本文 |
| 日本語説明 | `Description`。画面の別欄に出す編集可能なメモ。生成には送らない |
| 元画像プロンプト | 元画像のメタデータから得られる生成文。画像そのものの解析結果ではない |

通常モードでは選択部分に背景色が付き、OFFの文は取り消し線で残ります。「編集」を押すとボタンが点灯し、括弧付きの本文を編集できます。もう一度押すと選択モードに戻ります。本文と別の操作欄に同じカメラ指示を重ねるのではなく、本文にある選択肢を操作する設計です。

## 2. 保存場所とファイル全体

標準保存先は `%LOCALAPPDATA%\PhotoViewer.Wpf\ai-styles.json` です。アプリのスタイル管理にある保存ファイルを開く操作からも確認できます。`PhotoViewer.Wpf` は互換性のための保存名です。

- ファイル全体はJSONオブジェクトで、`Version` は数値の `1`。
- 動画スタイルは `VideoStyles` 配列に入れます。動画スタイルだけの専用ファイルとは限らず、`PhotorealStyles`、`I2iEditStyles`、`VideoEditV2Styles` や各選択名が共存します。
- **配布見本で既存ファイル全体を上書きしないこと。** 編集対象のスタイルだけを変更し、他の配列・選択名・互換な未知フィールドを保持します。
- スタイル件数の固定上限はありません。ファイル全体は最大4 MiBです。無限に保存できるという意味ではありません。
- スタイル名は前後の空白を除いて1〜40文字。制御文字不可。同じ名前は大文字小文字を区別せず重複扱いになるため、一意にします。日付付きの名前に特別な実行上の意味はありません。
- JSONのキーは本書どおりの大文字小文字を使います。コメント、末尾カンマ、重複キー、将来バージョンへの書き換えは使いません。JSONの `true` / `false` を文字列にしません。
- 外部編集では最新ファイルを読み直し、変更差分を確認します。保存途中の競合を避けるため、アプリ内での同時スタイル編集は避けてください。

外側のスタイルで使うフィールドは次のとおりです。

| キー | 新規H3スタイルの値・用途 |
|---|---|
| `Name` | 一意な表示名、1〜40文字 |
| `ModelId` | `"minimax-h3"` |
| `QualityId` | `"wan22-ti2v-5b-normal-v1"` または `"wan22-ti2v-5b-high-v1"`。旧名称の互換識別子なので、H3らしい名前に推測で改名しない |
| `DurationSeconds` | 名目の `5` / `10` / `12` / `15` |
| `PlaybackFps` | **保存形式の互換値**。現行読込処理が受け付ける値は `12` / `16`、新規見本では `16`。H3出力は別に24fps固定。ここを推測で `24` にすると現行スタイル読込では受理されない |
| `MaximumPixelArea` | `230400` / `307200` / `414720`。面積上限であり幅・高さではない |
| `Steps` | `1`〜`40`、通常 `20`。省略・null・範囲外は現行正規化で20になる。既存の有効値は保持する |
| `Prompt` | 現在の生成用本文を保持する互換フィールド。新規の注釈付き見本では既定選択時の完全なH3本文を入れる |
| `InstructionProgram` | 下記の編集用オブジェクト。省略/nullの旧スタイルも読める |

`Prompt` だけを書き換えても、有効な `InstructionProgram` があれば、選択状態から本文が再構成されます。選択機能を編集するときは `Template` と `Options` を編集します。`AnnotatedH3` がtrueの場合、元の比較用ベースを現在の選択結果で毎回上書きしません。

H3は24fpsで、名目5秒は124フレーム、10秒は243、12秒は294、15秒は362フレームです。15秒設定の実長は約15.083秒です。保存用の互換FPSと混同しないでください。

## 3. コピーして読める完全なスタイル見本

これは**新しい独立した見本ファイル**として有効な形です。既存の `ai-styles.json` に入れる場合は、`VideoStyles` の中の1要素を追加・編集対象にします。

初期選択は固定カメラと微笑みです。「手を振る」は初期OFFです。共通の表情メニューを使うと、本文の表情文だけが置き換わります。AnimeとPhotoを一つの名前で扱えます。

```json
{
  "Version": 1,
  "VideoStyles": [
    {
      "Name": "Example_CameraGreeting",
      "ModelId": "minimax-h3",
      "QualityId": "wan22-ti2v-5b-normal-v1",
      "DurationSeconds": 5,
      "PlaybackFps": 16,
      "MaximumPixelArea": 414720,
      "Steps": 20,
      "Prompt": "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.\n\nintegrated_multimodal_description: [Shot 1] A 2D-animated scene matching the reference image. Keep the camera fixed. Preserve the woman's appearance, clothing, and surroundings. She smiles. She looks toward the camera.\n\n\noverall_soundscape: Quiet room ambience.\n\nnon_diegetic_music: N/A",
      "InstructionProgram": {
        "Version": 1,
        "Enabled": true,
        "UseSourceVariants": true,
        "AnnotatedH3": true,
        "Template": "For the target video, at 0.00 seconds into the target video, <Picture 1> (from \\[Shot 1\\]) is fully referenced.\n\nintegrated_multimodal_description: \\[Shot 1\\] A 2D-animated scene matching the reference image. [Keep the camera fixed. / Slowly move the camera closer. / Slowly orbit left around the subject.] Preserve the woman's appearance, clothing, and surroundings. [She smiles. / She looks curious.] She looks toward the camera.\n[She gives one small wave.]\n\noverall_soundscape: Quiet room ambience.\n\nnon_diegetic_music: N/A",
        "PhotorealTemplate": "For the target video, at 0.00 seconds into the target video, <Picture 1> (from \\[Shot 1\\]) is fully referenced.\n\nintegrated_multimodal_description: \\[Shot 1\\] A live-action scene matching the reference image. [Keep the camera fixed. / Slowly move the camera closer. / Slowly orbit left around the subject.] Preserve the woman's appearance, clothing, and surroundings. [She smiles. / She looks curious.] She looks toward the camera.\n[She gives one small wave.]\n\noverall_soundscape: Quiet room ambience.\n\nnon_diegetic_music: N/A",
        "BaseH3Template": "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.\n\nintegrated_multimodal_description: [Shot 1] A 2D-animated scene matching the reference image. Keep the camera fixed. Preserve the woman's appearance, clothing, and surroundings. She smiles. She looks toward the camera.\n\n\noverall_soundscape: Quiet room ambience.\n\nnon_diegetic_music: N/A",
        "PhotorealBaseH3Template": "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.\n\nintegrated_multimodal_description: [Shot 1] A live-action scene matching the reference image. Keep the camera fixed. Preserve the woman's appearance, clothing, and surroundings. She smiles. She looks toward the camera.\n\n\noverall_soundscape: Quiet room ambience.\n\nnon_diegetic_music: N/A",
        "Description": "固定カメラで微笑む見本。カメラと表情を文中で選べる。手を振る動作は初期OFF。",
        "PhotorealDescription": "同じ動作を実写として扱う見本。日本語の説明は生成に送らない。",
        "ActionSamples": "",
        "SourceRules": false,
        "ImageChoices": false,
        "ActionPlot": false,
        "PhysicalContinuity": false,
        "OpeningMotionId": "original",
        "ArmMotionId": "original",
        "ExpressionId": "original",
        "MoodId": "original",
        "OriginalDefault": "anime",
        "PreferredLoraId": "",
        "Options": {
          "[Keep the camera fixed. / Slowly move the camera closer. / Slowly orbit left around the subject.]": {
            "Label": "カメラ",
            "Category": "camera",
            "ChoiceLabels": [
              "固定",
              "ゆっくり寄る",
              "左へ回り込む"
            ],
            "Mode": "on",
            "ChoiceIndex": 0
          },
          "[She smiles. / She looks curious.]": {
            "Label": "表情",
            "Category": "expression",
            "ChoiceLabels": [
              "微笑む",
              "興味深そうにする"
            ],
            "Mode": "on",
            "ChoiceIndex": 0,
            "DirectionAspect": "expression",
            "DirectionReplacement": ""
          },
          "[She gives one small wave.]": {
            "Label": "手を振る",
            "Category": "action",
            "Mode": "off",
            "DefaultOn": false,
            "DirectionAspect": "arms",
            "DirectionReplacement": ""
          }
        }
      }
    }
  ]
}
```

初期状態で選択を解決した結果は、`BaseH3Template` と同じです。カメラの `ChoiceIndex` を `1` にすると寄るカメラへ変更できます。`[She gives one small wave.]` の `Mode` を `"on"` にすると、その文が加わります。

## 4. 本文内の記法

| 記法 | 意味 | 例 |
|---|---|---|
| 普通の文章 | そのまま残す | `She looks toward the camera.` |
| `[文]` | 一つの文・句を使う／使わない | `[She gives one small wave.]` |
| `[候補A / 候補B]` | 手動で一つ選ぶ。全体をOFFにもできる | `[She smiles. / She looks curious.]` |
| `{候補A / 候補B}` | 画像に基づくAI選択の候補。手動固定やOFFも可能 | `{takes one small step / remains in place}` |
| `\[` / `\]` / `\{` / `\}` | 文字として括弧を残す | `\[Shot 1\]` |

### 4.1 区切りと禁止事項

- `［］`・`｛｝`も受け付けますが、新規編集は半角に統一すると照合しやすくなります。
- `[]`を選択肢にするには、少なくとも一箇所に半角スペースを伴う ` / ` または ` ／ ` が必要です。`[A/B]` は一つのON/OFF文です。
- `{}`は常に選択肢です。候補は2〜16個。`{A}` は無効です。候補を空にしてOFFを表現せず、`Mode: "off"` を使います。
- 選択肢と判定された後は、内部の `/` と `／` がすべて区切りになります。**候補の文章中にURLや分数のスラッシュを入れないでください。** `\/` で選択肢内のスラッシュを保護する機能はありません。
- 入れ子、違う種類の閉じ括弧、空の括弧、閉じ忘れは不可です。`[A {B / C}]` は使えません。
- 一つの本文中でオプションの出現は128個まで。括弧の中は、前後空白除去後1〜1000文字です。
- `\` は直後の一文字をリテラルにする記号です。文字としてのバックスラッシュも適切に二重化します。括弧付きの正規表現やプログラムを実行する機能ではありません。
- 構文が未完成の草稿を保存できる場合はありますが、**保存できたことは生成できることの証明ではありません**。展開時に構文と上限が検証されます。

### 4.2 `Options` のキーは文章そのもの

IDを別に付ける方式ではありません。パーサーが読んだ括弧全体がキーです。

```text
Template 内: [She smiles. / She looks curious.]
Options キー: [She smiles. / She looks curious.]
```

キーは括弧を半角にし、括弧内の前後空白を除き、エスケープを解いた文字列で作ります。途中の空白、大文字小文字、句読点は照合対象です。同じキーが複数箇所・両方の画像バリアントに出れば、同じ設定を共有します。

本文の候補を変更したら、キー、`ChoiceLabels`、`ChoiceIndex`、必要なら `DirectionReplacement` も更新してください。独立して操作したい別の箇所に同じキーを使わないこと。任意ID、`[id: ...]`、`${variable}`などに特別な意味はありません。

キーが登録されていない場合、`[]` はON、`{}` はauto、候補番号は0として扱われます。登録されているが本文に出ないキーは出力を作りません。誤字でも保存できる場合があるので、全キーを対応する本文トークンと照合してください。

### 4.3 OFFでも文章が壊れない設計

AI強化OFFは文法をAIで修正しません。選択した文字列を連結し、外側の空白を整えるだけです。句読点や接続語は、削除したい部分と一緒に括弧へ入れます。

```text
扱いやすい: She looks toward the camera. [She gives one small wave.] She stays in place.
注意が必要: She [smiles / looks curious] and looks toward the camera.
```

後者で括弧全体をOFFにすると `She  and looks ...` が残ります。既存文を移行する場合は原文の意味を保って文の単位を選び、ON/OFFの両方を確認します。新規見本は独立した文を候補にして、この問題を避けています。

## 5. オプションのフィールド

`Options` は「トークンのキー → 次のオブジェクト」という辞書です。省略時の既定値は以下のとおりです。ただし、辞書エントリー自体がない `{}` の既定Modeだけは `auto` になります。

| フィールド | 既定値 | 意味・有効値 |
|---|---|---|
| `Label` | `""` | 操作用の日本語名。生成文に入らない。最大120文字 |
| `Category` | `""` | `""`, `camera`, `action`, `expression`, `viewpoint`, `ending`, `sound`, `detail`。見た目・編集上の分類であり、指示を自動で追加する機能ではない |
| `ChoiceLabels` | `[]` | 候補と同じ順番の日本語表示名。最大16個、各120文字。見本では候補数と一致させる |
| `Mode` | `"on"` | `on`, `off`, `auto`。`on` は採用・手動固定、`off` は不採用、`auto` は記号に応じた自動処理 |
| `DefaultOn` | `true` | `[]` のauto条件を使わない／元情報が不明のときのON/OFF既定値 |
| `Condition` | `"contains"` | `contains`, `absent`, `has-prompt`, `no-prompt`。`[]` のautoで使う |
| `Keyword` | `""` | `contains` / `absent` の検索文字列、最大200文字 |
| `ChoiceIndex` | `0` | 0始まりの候補番号。0〜15かつ実際の候補数未満 |
| `Group` | `""` | 手動でONにしたとき、同じグループの別トークンをOFFにする。最大120文字 |
| `DirectionAspect` | `""` | 共通演出メニューとの対応。`""`, `opening`, `arms`, `expression`, `mood`, `camera`, `capture`。未指定のCategory=cameraもカメラ指示に対応 |
| `DirectionReplacement` | `""` | 対応する共通演技を選んだとき、その箇所に残す接続用の文。最大1000文字。Aspectなしでは非空にできない |

`Group` は取込時に矛盾したONを自動修復する機能ではありません。同じグループを使うなら、配布時から既定状態を整えます。手動でONにしたときに限り、現在のバリアント本文に現れる同グループの別項目をOFFにします。候補一組だけなら `[A / B]` の方が簡単です。

配色はUIが決めます。`{}`は紫、条件autoの`[]`は黄、カメラ・視点は緑系、表情は桃系、終わり方は橙系、通常の選択は青系です。任意の `Color` キーを追加しても色指定として動作しません。

## 6. 自動条件と画像AI選択は別の機能

### 6.1 元画像プロンプトでON/OFFする `[]`

次の三つがそろうと条件を評価します。

1. `InstructionProgram.SourceRules` がtrue。
2. 対象が `[]`。
3. そのオプションの `Mode` が `"auto"`。

| Condition | 判定 |
|---|---|
| `contains` | 元画像プロンプトにKeywordを含む |
| `absent` | 元画像プロンプトにKeywordを含まない |
| `has-prompt` | 元画像プロンプトが空白だけでなく存在する |
| `no-prompt` | 元画像プロンプトが既知の空文字・空白である |

検索は大文字小文字を区別しない単純な部分一致です。正規表現、単語境界、否定の意味理解、タグの重み付け、AND/OR式はありません。`Keyword: "umbrella"` は `no umbrella` にも一致します。`contains` / `absent` のKeywordが空だと条件はfalseです。

元画像プロンプトが**取得できていない**場合は「ない」と断定せず `DefaultOn` を使います。SourceRulesがfalseでもautoはDefaultOnへ戻ります。Modeを手動on/offにした場合は手動が優先です。条件自体はローカル判定なので、AI強化OFFでも使えます。通常のキュー登録は条件判定のためにLLMを待ちません。

### 6.2 画像から候補を選ぶ `{}`

画像AI選択は、**キュー追加ボタン上のAI強化ON**、`ImageChoices: true`、対象の `Mode: "auto"` がそろうと、強化処理で行われます。画像に適合する候補を一つ選ぶ指示をLLMへ渡します。

AI強化OFF、ImageChoices=false、またはMode=onなら `ChoiceIndex` の候補を使います。Mode=offなら全体を除きます。`{}` のautoでは `Condition` / `Keyword` / `DefaultOn` によるON/OFFは行いません。

`[]`にautoを付けても画像選択にはなりません。`{}`を書くだけでもローカルAIが勝手に起動するわけではありません。AI選択はモデルへの指示であり、見えていない状態まで正確に判定する保証ではありません。

### 6.3 自動処理を組み合わせた見本

次はファイル全体ではなく、**`InstructionProgram` の値そのもの**です。`AnnotatedH3: false` の簡潔な指示形式です。AI強化OFFならその場にとどまる候補を使い、元プロンプトに `umbrella` が含まれる場合だけ傘の文を入れます。セリフは初期OFFです。

```json
{
  "Version": 1,
  "Enabled": true,
  "UseSourceVariants": false,
  "AnnotatedH3": false,
  "Template": "Preserve the reference woman's appearance, clothing, and surroundings. Keep the initial camera composition. The woman {takes one small step forward / remains in place}. [She keeps the umbrella steady.] [She says <d>\\[Japanese\\] こんにちは。</d>.]",
  "Description": "元プロンプトの条件、画像AI選択、固定セリフのON/OFFを区別する見本。",
  "SourceRules": true,
  "ImageChoices": true,
  "ActionPlot": true,
  "PhysicalContinuity": true,
  "ActionSamples": "Begin from the reference pose. Make at most one small position adjustment, then settle naturally.",
  "Options": {
    "{takes one small step forward / remains in place}": {
      "Label": "画像に合う冒頭動作",
      "Category": "action",
      "ChoiceLabels": [
        "一歩前へ",
        "その場にとどまる"
      ],
      "Mode": "auto",
      "ChoiceIndex": 1,
      "DirectionAspect": "opening",
      "DirectionReplacement": "remains in the reference pose initially"
    },
    "[She keeps the umbrella steady.]": {
      "Label": "傘が記載されている場合",
      "Category": "detail",
      "Mode": "auto",
      "DefaultOn": false,
      "Condition": "contains",
      "Keyword": "umbrella"
    },
    "[She says <d>[Japanese] こんにちは。</d>.]": {
      "Label": "短い挨拶",
      "Category": "sound",
      "Mode": "off",
      "DefaultOn": false
    }
  }
}
```

最後のオプションのキーでは `[Japanese]` がエスケープなしなのに、Templateではエスケープされている点に注意してください。**キーはパーサーがエスケープを解いた後の文字列**だからです。

この短文形式をAI強化OFFで使うと、音・音楽の欄はN/Aで包まれます。セリフのON/OFFは記法の見本です。実際に声や環境音まで指定するスタイルでは、セクション10の完全なH3形式を使い、音欄とセリフの指示も整合させてください。

## 7. InstructionProgram 全フィールド

キーを省略した場合の初期値です。nullで空文字を代用しないでください。互換な未知フィールドは保存できますが、新しいキーを書いたからといって機能が追加されるわけではありません。

| キー | 既定値 | 用途 |
|---|---|---|
| `Version` | `1` | 指示言語の形式。現行は1のみ |
| `Enabled` | `false` | Aibosの選択言語を使う。AI強化チェックとは別 |
| `UseSourceVariants` | `false` | 画像種別によるベース本文・説明の切替を使う。二つのバリアントを作る場合はtrue |
| `AnnotatedH3` | `false` | trueならTemplateが完全なH3本文への注釈。Baseを二重に追加しない |
| `Template` | `""` | Original/アニメ側、または共通の編集用本文 |
| `PhotorealTemplate` | `""` | 実写側の編集用本文。空・空白ならTemplateへフォールバック |
| `BaseH3Template` | `""` | Original側の完全なH3本文。注釈付きでは比較用原文 |
| `PhotorealBaseH3Template` | `""` | 実写側のベース本文。空・空白ならBaseH3Templateへフォールバック |
| `Description` | `""` | Original側または共通の日本語説明、生成には送らない |
| `PhotorealDescription` | `""` | バリアント使用時の実写側説明。空なら空の説明であり、Descriptionへ自動フォールバックしない |
| `ActionSamples` | `""` | AIが秒数付き動作を考える際の参考例。ActionPlot有効時のみ強化指示に含める |
| `SourceRules` | `false` | 元画像プロンプトから `[]` のauto条件を判定 |
| `ImageChoices` | `false` | AI強化時に `{}` のauto候補を画像から選択 |
| `ActionPlot` | `false` | AI補完時にActionSamplesを細部の参考にする。新しい主動作を作る許可ではない |
| `PhysicalContinuity` | `false` | AI強化時に支持・接触・重力・慣性・収束の連続性を指示 |
| `OpeningMotionId` | `"original"` | 冒頭の身体の動き。付録の登録IDのみ |
| `ArmMotionId` | `"original"` | 腕・手の冒頭動作や小さなしぐさ。付録の登録IDのみ |
| `ExpressionId` | `"original"` | 区間なしの場合の表情。付録の登録IDのみ |
| `MoodId` | `"original"` | 区間なしの場合の雰囲気。付録の登録IDのみ |
| `CameraMotionId` | `"original"` | 区間なしの場合のカメラワーク。VideoDirectionTimeline.Camerasの登録ID |
| `CaptureModeId` | `"original"` | 動画全体の撮り方。`original`, `pov`, `handheld`, `stabilized`, `phone`, `documentary`, `shoulder`, `body-mounted` |
| `DirectionPhases` | `[]` | 共通時間軸の最大3区間。空なら上記の全体設定を使う。各区間の項目は後述 |
| `OriginalDefault` | `"anime"` | Original入力の既定扱い。`anime` / `photoreal` |
| `PreferredLoraId` | `""` | 推奨LoRAのメモ。生成文に送らず、自動ロードもしない |
| `Options` | `{}` | オプション設定辞書 |

### 7.1 三つの本文の使い方

| 用途 | 設定と動作 |
|---|---|
| 既存の完全なH3を選択可能にする | `Enabled=true`, `AnnotatedH3=true`。Templateには完全なH3に括弧を付けたもの。Baseには元の完全なH3。既存スタイルの移行に向く |
| 短い指示からH3へ組み立てる | `Enabled=true`, `AnnotatedH3=false`。Baseが空なら、H3見出しのない普通の本文に冒頭・映像・音・音楽の枠を付ける。音・音楽の既定はN/A |
| 元のH3へ追加の映像指示を足す | `Enabled=true`, `AnnotatedH3=false`。Baseに完全なH3、Templateに追加分だけ。二重指示を避けるため、既存全文をTemplateにも入れない |

`Enabled=false` なら言語として展開しません。UseSourceVariants=trueの場合は種別に応じたBaseを文字どおり使用し、それ以外はPromptを使用します。

有効なプログラムのTemplate/Base選択関数は実写種別と非空の実写フィールドで選びます。`UseSourceVariants=false` だけで、残っている `PhotorealTemplate` が必ず無視されると仮定しないでください。共通本文にしたいなら実写側フィールドを空にします。

H3の見出しが一部分だけ存在する本文を、自動で完全修復する仕組みではありません。既存H3を扱う場合は完全な構造を維持します。

### 7.2 Sourceの優先順位

1. 今回の入力画像に対して指定した一時的な「アニメ／実写」の手動選択。
2. アプリが実写化出力として確認できる画像なら実写。
3. それ以外のOriginal入力は `OriginalDefault`。

元から実写の画像を画像認識で自動分類する仕様ではありません。必要なら今回だけ手動で実写にします。この指定は元画像に書き込まず、画像の恒久的な分類として記憶せず、正常なキュー追加後に解除されます。

### 7.3 AI強化の範囲

ActionPlotはActionSamplesを細部の参考に使う指定です。例を全部追加したり、元の動作を大きく・速くする指定ではありません。AI補完OFF時はActionPlotとPhysicalContinuityの追加指示を出しません。

PhysicalContinuityは物理シミュレーターでも必須LoRAでもありません。元画像を初期状態として扱い、支持・接触を守り、明示された実行可能な解放や既に支持されていない変位に限って連続した動きを指示します。支持状態が不明なら、勝手な解放や静止位置・初速を想像しません。

AI補完後の本文はジョブの実行用コピーです。保存済みスタイル、注釈前の本文、日本語説明、送信済み要求を上書きしません。AIの出力が使えなければ一度だけ補正を試し、適用できない場合は元の本文と適用済みの手動設定で生成を続けます。診断に理由を記録し、補完の失敗だけで動画全体を止めません。

## 8. 冒頭の動き・腕・表情・ムードと本文の重複

共通の演技メニューはすべてのスタイルで使えます。`original` は何も追加せず、元の本文を使います。別のIDを選ぶと、映像の記述部分へ対象を限定した指示を追加します。

| 設定 | 対象範囲 |
|---|---|
| 冒頭の動き | 元画像の直後、最初の1〜2秒の身体の移動や姿勢。動画全体で繰り返す指示ではない |
| 腕の動き | 元姿勢からの腕・手の動き、その後の姿勢・小さなしぐさ。身体全体の移動ではない |
| 表情 | 主動作に反応する顔の方向性。区間があればその区間に適用し、なければ動画全体に適用する。顔を固定する指示ではない |
| ムード | 既存の主動作をどんな調子で行うか。新しい出来事や主動作を追加するものではない |

主動作に必要な手の動き、物を持つこと、支持や接触が、飾りの腕のしぐさより優先です。表情とムードが競合する顔の指示では、明示した表情が優先します。現在の共通演技カタログは女性の被写体向けの英文です。他の被写体へそのまま一般化するIDではありません。

### 8.1 重複を除くための明示的な結び付け

本文を自動で意味分類して削除する機能はありません。どの句が共通メニューに対応するかを編集者が判断し、そのトークンに `DirectionAspect` を付けます。

```text
本文: [She smiles. / She looks curious.]
設定: DirectionAspect = "expression", DirectionReplacement = ""
```

- ExpressionId=originalなら本文の選択を使います。
- ExpressionIdを別の登録IDへ変更すると、対応した元の句を生成用コピーから除き、共通の表情指示を使います。
- `DirectionReplacement` があれば、除く代わりにその文字列を残します。選択した表情自体は共通指示として別に挿入されます。
- 元のトークン、Mode、本文は編集データに残り、originalへ戻すと復元できます。
- `DirectionReplacement` は既に展開済みの接続文です。そこへ新しい `[]`・`{}` を入れて再帰展開させないでください。

例えば `The woman [is smiling and] looks toward the camera.` のような句なら、表情だけを置き換える場合のReplacementは空です。`She [smiles and looks toward the camera.]` なら、視線を残すため `DirectionReplacement: "looks toward the camera."` が必要になる可能性があります。文法だけでなく、選択の対象外である動作を失っていないかを確かめます。

一つの文に表情、腕、主動作が混ざっている場合、一文丸ごとをexpressionとして消さないこと。意味の境界で対象を絞り、残す部分を明示します。一つのトークンへ複数Aspectを指定する機能はありません。

## 9. カメラ、視点、Additionalの整理

既存の文中カメラ選択肢は保持します。`Category=camera` または `DirectionAspect=camera` のトークンは、共通のカメラワークに対応します。別の動きを選んだ区間ではその句を置き換え、`original` の区間では元の選択を使います。`viewpoint` だけでは置換の対象になりません。

候補として固定、寄る、引く、左右へ回り込む、正面へ移る、追従、低い／高い角度、手持ち風、POV風などを書けます。意味を持つのは候補の英文であり、日本語Labelだけ変えても動きは変わりません。POV風は撮影者側の位置・頭の揺れ・視線移動として記述し、被写体の移動と分けます。

`DirectionPhases` の各要素は `EndMillionths`, `CameraId`, `ArmsId`, `ExpressionId`, `MoodId` を持ちます。終了位置は全体を1,000,000とした整数で増加させ、最後は必ず1,000,000です。最初の開始は0、次の開始は前の終了です。動画尺が変われば割合を保って実際の秒数へ変換します。区間がある場合、全体用のCameraMotionId・ArmMotionId・ExpressionId・MoodIdより区間の設定を使います。

```json
"DirectionPhases": [
  { "EndMillionths": 200000, "CameraId": "front", "ArmsId": "lower", "ExpressionId": "surprised", "MoodId": "dramatic" },
  { "EndMillionths": 600000, "CameraId": "upper-body", "ArmsId": "lower", "ExpressionId": "suspicious", "MoodId": "dramatic" },
  { "EndMillionths": 1000000, "CameraId": "face", "ArmsId": "still", "ExpressionId": "calm", "MoodId": "soft" }
]
```

これは主動作を3回書く指定ではありません。共通の主動作を一度残し、各時間帯の変化を映像欄へまとめます。同じ腕の動作が続く場合は繰り返さず、変更区間だけを記述します。新規AIセリフは同じ区間内へ割り当て、既存セリフは分割・複製しません。区間の `original` は元のスタイル指示へ戻る意味で、直前の選択を引き継ぐ指定ではありません。

撮り方を変更すると、明示的なカメラトークン内の既知の手持ち・微振動の句だけを置き換えます。自由文の意味を推測して削除する機能ではありません。各IDの英語の定義は `VideoSubjectDirection.cs` と `VideoDirectionTimeline.cs` を正本にします。

`Additional action` / `Additional event` という見出しに特別な機能はありません。既存の追加指示を選択可能にする場合は、見出しとその本文の対象範囲を確認し、まとまりごとに括弧へ入れます。OFFで見出しだけ・接続語だけが残らないようにします。独立して併用できる追加指示は別々の `[]`、どれか一つなら `[A / B]` またはGroupを使います。構造化だけを依頼された場合、選択肢化を理由に内容を増減しません。

## 10. H3の冒頭・音・日本語セリフ

最終H3の基本形は次のとおりです。これは**モデルへ渡す結果**なので、Aibos編集用のエスケープはありません。

```text
For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.

integrated_multimodal_description: [Shot 1] Preserve the reference image's appearance and composition. The woman looks toward the camera and says <d>[Japanese] こんにちは。</d>.

overall_soundscape: Quiet room ambience and soft fabric movement.

non_diegetic_music: N/A
```

- 最初の一文は参照画像を0.00秒に結び付けます。見た目を軽くするために削除する説明文ではありません。
- `integrated_multimodal_description:` は映像・動作・セリフを含む場面の記述。
- `overall_soundscape:` は場面内の音。`non_diegetic_music:` はBGM。BGMなしは `N/A`。
- 日本語説明を分離しても、本文中の `<d>[Japanese] ...</d>` は生成するセリフとして残します。日本語という理由だけでDescriptionへ移しません。
- `Template` に書く場合はH3の参照括弧を `\[Shot 1\]` にします。JSONの文字列では `\\[Shot 1\\]` です。生の `[Shot 1]` / `[Picture 1]` は編集用パーサーで明示的に拒否されます。
- `[Japanese]` は生で書くと手動トークンとして解釈され得ます。リテラルの言語タグとして `\[Japanese\]` にし、JSONでは `\\[Japanese\\]` にします。
- `BaseH3Template` と `Prompt` は最終H3の文字列なので、上記の編集用括弧エスケープは不要です。JSONとしての改行や引用符のエスケープは必要です。

### 10.1 三層のエスケープ

| 場所 | 見える文字列 |
|---|---|
| JSONファイルのTemplate文字列 | `"... \\[Shot 1\\] ..."` |
| JSONを読んだ後の編集本文 | `... \[Shot 1\] ...` |
| 選択を解決したH3 | `... [Shot 1] ...` |

JSONの改行は `\n`、引用符は `\"`。JSONのエスケープとAibos言語のエスケープを別々に考えてください。`&#x20;` のようなHTMLエンティティを復号する機能はありません。

**画像から日本語セリフを自動生成する専用フィールドは、現行スキーマにありません。** 固定のセリフや候補を本文に持たせることはできますが、`AutoDialogue`、`DialogueLanguage`などを追加して実装済みの機能として扱わないでください。日本語説明の自動翻訳も未接続です。

## 11. LoRAとスタイルJSONの境界

動画メニューのLoRA欄では、保存フォルダからファイルを選び、有効チェックと強度を指定します。行は追加ボタンで増やせます。保存フォルダをエクスプローラーで開く操作もあります。実際の適用は、選んだファイルと検証済みの対応ランタイムを使う別の仕組みです。

`PreferredLoraId` はメモです。ここへファイル名やモデル名を書いても、検索・ダウンロード・選択・ロードは発生しません。`InstructionProgram` の中に未定義の `Loras` や `LoraStrength` を作らないでください。実使用のLoRA選択は動画メニューで行います。汎用的に全スタイルへ必須LoRAを強制するフィールドもありません。

LoRAの個別の互換性、効果、推奨強度はこの文法からは判断できません。別のAIに調査を依頼する場合は、モデルの正式な配布先、H3の対応方式、必要ランタイム、ファイル名とハッシュ、サイズ、ライセンス、作者推奨値、未検証点を別の調査結果として提出させます。調査候補を導入済み・適用済みと混同しないこと。

## 12. 上限と失敗しやすい箇所

| 対象 | 上限・制約 |
|---|---|
| スタイルファイル全体 | 4 MiB |
| スタイル名 | 1〜40文字、一意 |
| Template / PhotorealTemplate / BaseH3Template / PhotorealBaseH3Template / Description / PhotorealDescription | 各8000文字 |
| ActionSamples | 4000文字 |
| PreferredLoraId | 200文字 |
| Options辞書 | 128エントリー |
| 本文中のトークン | 128出現。繰り返しの同一キーも出現数に数える |
| トークン内容 / キー | 内容1000文字、キー1002文字 |
| 候補 | 2〜16個、空候補不可。単一ON/OFFの `[]` に候補配列は不要 |
| 展開後の直接H3 | 8000文字 |
| AI向け展開指示 | 8000文字かつUTF-8で14000バイト。追加の強化要求にも別の長さ検証があるため、上限ぎりぎりにしない |

ここでいう文字数は実装の.NET文字列長、すなわちUTF-16コード単位です。一部の絵文字などは見た目の一文字が二単位になります。制御文字は改行CR/LFとタブ以外禁止です。上限超過を黙って切り捨てて内容を変えないでください。

| 症状 | 確認すること |
|---|---|
| スタイルが出てこない | 外側のQualityId・互換PlaybackFps・名前・Version・InstructionProgramの型・登録ID |
| 色付き箇所の日本語ラベルが付かない | Templateから作られるキーとOptionsのキーが一致するか |
| 候補を選べない | 区切りに ` / ` を使っているか、入れ子や空候補がないか |
| AI OFFで追加できない | H3候補の準備は必須ではない。構文、空の展開結果、候補番号、長さ、参照元の変更など実際のエラーを確認 |
| 波括弧なのにAIが選ばない | AI強化、ImageChoices、対象Mode=autoがそろっているか |
| 共通表情と本文が矛盾する | 該当句のDirectionAspectとReplacement、無関係な直書きの重複 |
| 音欄へ動きが混入する | H3見出しは行頭に一つずつ、正しい順序で。引用符と `<d>` の閉じ忘れも確認 |
| 15秒が15.083秒になる | H3の362フレーム÷24fpsによる仕様 |

## 13. 既存スタイルを編集するAIへの手順

1. 入力JSONを構文解析する。Version、外側の設定、未知フィールドを把握する。入力にない本文や画像を推測して作らない。
2. 編集範囲を決める。構造化のみの依頼と、内容の改善・新しい候補の追加の依頼を区別する。既存の意味・登場者・主動作・音・セリフ・否定条件を勝手に変えない。
3. 編集前の各バリアント本文を比較用に保つ。日本語訳として明確に区切られた末尾説明だけをDescriptionへ分離する。本文中の日本語セリフは残す。
4. PhotoとAnimeを統合する場合は、互換な生成設定と同じ意図のスタイルか確認し、一つのスタイル内に各バリアントを保持する。設定が違うものを勝手に一方へ合わせない。
5. ユーザーが選びたい箇所は `[]`、画像から一つ選ぶ候補は `{}`、固定の主動作は普通の本文として置く。条件検索で分かることと画像判断が必要なことを分ける。
6. カメラ、冒頭、腕、表情、ムード、追加指示の既存の重複を調べる。本文の対応箇所を明示的に選択化・Aspect対応させ、対象外の内容を削除しない。
7. 各トークンへOptionsを付ける。原文を再現する既定選択、初期OFFの追加候補、候補ラベル、条件不明時の既定値を明示する。
8. 両方のバリアントで、既定状態、各ON/OFF、候補選択、共通演技への切替、AI OFFを確認する。初期状態を保持する移行では、展開結果と比較用原文の差分を取る。
9. 不明な条件や曖昧な依存関係は未解決点として報告する。JSONの新しいキーを発明して「対応できた」としない。
10. 有効なJSONと短い変更表を返す。変更表は「場所／元の意味／操作方法／初期状態／内容変更の有無」。生成していなければ画質・動作が再現できたと書かない。

そのまま渡せる依頼文:

```text
添付の「Aibos Image 動画スタイルJSON・指示言語ガイド」と、編集対象JSONを読んでください。
現行Version 1で読み込める形を維持し、指定したスタイルを編集してください。
構造化だけを指定した箇所は元の意味を変えず、原文を再現する既定選択を残してください。
内容を改善する場合は依頼された範囲に限り、構造変更と内容変更を分けて報告してください。
手動選択、元プロンプト条件、画像AI選択、共通演技との対応を区別してください。
H3の冒頭・映像・音・音楽・セリフを保持し、日本語の説明だけを別フィールドへ分離してください。
既存の生成設定、未知フィールド、対象外スタイルを維持し、未実装キーを作らないでください。
最終出力は、完全で有効なJSON、変更箇所の表、確認したこと、未検証事項にしてください。
不足する本文や画像は推測せず、その箇所を明示してください。
```

## 14. 共通演技の登録ID一覧

以下は確認リビジョンの実装から採ったIDと画面名です。JSONには日本語名でなく左列のIDを入れます。どの欄も `original` は元の本文を保持します。任意IDや日本語名を直接追加してカタログを増やすことはできません。

### OpeningMotionId

| ID | 表示名 |
|---|---|
| `original` | 元の指示を使う |
| `approach` | こちらへ近づく |
| `approach-slow` | ゆっくり近づく |
| `step-back` | 後ずさる |
| `stay` | その場にとどまる |
| `sway` | 身体をゆっくり揺らす |
| `sway-rhythm` | リズムに合わせて揺れる |
| `tense` | 身体を強張らせる |
| `relax` | 力を抜く |
| `lean-in` | その場で身を乗り出す |
| `lean-back` | その場で身を引く |
| `turn-viewer` | こちらへ身体を向ける |
| `turn-away` | 少し身体をそらす |
| `weight-shift` | 重心を移す |
| `straighten` | 姿勢を正す |
| `small-startle` | 少し驚いて反応する |
| `pause` | 一瞬ためらってから動く |
| `step-side` | 横に一歩ずれる |
| `approach-brisk` | 軽快に一歩近づく |
| `half-turn` | 半身になる |
| `look-back` | 振り返る |
| `small-bow` | 軽くおじぎする |
| `nod` | 小さくうなずく |
| `head-tilt` | 首をかしげる |
| `shoulder-shrug` | 肩をすくめる |
| `shoulders-open` | 胸を張って姿勢を開く |
| `curl-in` | 少し身を縮める |
| `settle-seated` | 座った姿勢を整える |
| `slight-rise` | 上体を少し起こす |
| `bend-forward` | 少し前かがみになる |
| `knees-soften` | 膝の力をゆるめる |
| `heel-lift` | かかとを少し浮かせる |
| `breath-settle` | ひと呼吸おいて落ち着く |

### ArmMotionId

| ID | 表示名 |
|---|---|
| `original` | 元の指示を使う |
| `lower` | 腕をすっと下ろす |
| `lower-slow` | 腕をゆっくり下ろす |
| `behind-back` | 後ろで手を組む |
| `front-clasp` | 前で手を重ねる |
| `fold-arms` | 腕を組む |
| `unfold-arms` | 腕組みをほどく |
| `still` | 腕・手を動かさない |
| `relaxed` | 腕の力を抜く |
| `fidget-fingers` | 指先をモジモジさせる |
| `fidget-hands` | 手を小さく握り直す |
| `hold-wrist` | 片手でもう片方の手首を持つ |
| `hold-elbow` | 片手を反対の肘に添える |
| `hand-hip` | 片手を腰に添える |
| `hands-hips` | 両手を腰に添える |
| `hands-lap` | 膝の上に手を置く |
| `hands-thighs` | 太ももの上に手を添える |
| `tuck-hair` | 髪を耳にかける |
| `touch-hair` | 髪にそっと触れる |
| `touch-cheek` | 頬に手を添える |
| `hand-chin` | 顎に手を添える |
| `cover-mouth` | 口元に手を添える |
| `small-wave` | 小さく手を振る |
| `palm-up` | 手のひらを上に向ける |
| `open-hands` | 手を軽く広げる |
| `reach-forward` | 片手をそっと差し出す |
| `draw-hands-in` | 手を身体の近くに引く |
| `stretch-arms` | 腕を軽く伸ばす |
| `loosen-wrists` | 手首を軽くほぐす |
| `soft-fists` | 手を軽く握る |
| `unclench` | 握った手をゆるめる |
| `hands-heart` | 両手を胸元に重ねる |
| `small-gesture` | 会話するように手を動かす |

### ExpressionId

| ID | 表示名 |
|---|---|
| `original` | 元の指示を使う |
| `bright` | 明るい表情 |
| `cheerful` | 楽しそう |
| `gentle-smile` | 穏やかな微笑み |
| `laughing` | 笑いをこらえきれない |
| `playful` | いたずらっぽい |
| `confident` | 自信たっぷり |
| `proud` | 得意げ |
| `shy` | はにかんだ表情 |
| `embarrassed` | 照れくさい |
| `annoyed` | ムッとしている |
| `pouting` | ふくれっ面 |
| `irritated` | いらだっている |
| `stern` | きりっと厳しい |
| `serious` | 真剣 |
| `focused` | 集中している |
| `determined` | 決意を感じる |
| `curious` | 興味津々 |
| `expectant` | 期待している |
| `surprised` | 驚いている |
| `confused` | 戸惑っている |
| `uneasy` | 不安そう |
| `suspicious` | 疑いのまなざし |
| `somber` | 暗く沈んだ表情 |
| `sad` | 悲しげ |
| `lonely` | 寂しげ |
| `weary` | 疲れた表情 |
| `sleepy` | 眠たげ |
| `calm` | 落ち着いている |
| `neutral` | 淡々とした表情 |
| `thoughtful` | 考え込んでいる |
| `relieved` | ほっとしている |
| `warm-smile` | 温かく笑いかける |
| `beaming` | 満面の笑み |
| `subtle-smile` | 口元だけ少しほころぶ |
| `wry-smile` | 苦笑い |
| `smug` | 余裕のある笑み |
| `tender` | 慈しむような表情 |
| `attentive` | 熱心に見守る |
| `admiring` | 感心している |
| `awe` | 目を見張る |
| `doubtful` | 半信半疑 |
| `disappointed` | がっかりしている |
| `unimpressed` | あきれ気味 |
| `bored` | 退屈そう |
| `apologetic` | 申し訳なさそう |
| `wistful` | 切なげ |
| `tearful` | 泣きそうな表情 |
| `composed-smile` | 余裕を保った微笑み |
| `bashful-smile` | 照れ笑い |
| `resolute-soft` | 穏やかだが意志が強い |
| `blank-surprise` | きょとんとしている |
| `flustered` | 恥ずかしそうに目を伏せる |
| `melting-smile` | とろけるような微笑み |
| `knowing-grin` | 得意げな笑み（ドヤ顔） |
| `mischievous-grin` | いたずらそうににやり |
| `affectionate` | 愛おしそうなまなざし |
| `dazed` | ぼんやり夢見心地 |
| `trying-not-smile` | 笑みを隠そうとする |
| `quiet-delight` | うれしさがにじむ |

### MoodId

| ID | 表示名 |
|---|---|
| `original` | 元の指示を使う |
| `lighthearted` | 明るく軽やか |
| `lively` | 元気いっぱい |
| `friendly` | 親しみやすい |
| `soft` | やさしく穏やか |
| `relaxed` | 自然体でくつろいだ |
| `quiet` | 静かで落ち着いた |
| `elegant` | 上品でしなやか |
| `cool` | クールで淡々と |
| `bold` | 堂々と力強く |
| `playful` | お茶目で遊び心のある |
| `comedic` | コミカル |
| `awkward` | ぎこちなく不器用 |
| `reserved` | 控えめで遠慮がち |
| `reluctant` | 気乗りしない様子 |
| `tense` | 張りつめた空気 |
| `serious` | 真面目で真剣 |
| `dramatic` | ドラマチック |
| `melancholy` | 物憂げでしっとり |
| `nostalgic` | 懐かしさのある |
| `mysterious` | ミステリアス |
| `dreamy` | 夢見心地 |
| `casual` | 気取らず普段どおり |
| `polite` | 丁寧で礼儀正しい |
| `graceful` | ゆったり優雅 |
| `precise` | きびきび正確 |
| `careful` | 慎重に確かめながら |
| `eager` | 積極的で前向き |
| `bashful` | 照れながら控えめに |
| `earnest` | 一生懸命に |
| `hesitant` | ためらいがちに |
| `solemn` | 厳かで静か |
| `cool-headed` | 冷静で余裕のある |
| `unhurried` | のんびりマイペース |
| `restless` | そわそわ落ち着かない |
| `reassuring` | 包み込むように穏やか |
| `curious` | 探るように興味深く |
| `restrained` | 感情を抑えた |
| `romantic` | ロマンチック |
| `seductive` | 魅惑的（セダクティブ） |
| `intimate` | 親密でやわらかい |
| `flirtatious` | 小悪魔のようにお茶目 |
| `cinematic` | 映画のワンシーンのように |
| `suspenseful` | 緊張感を含んだ |

### CameraMotionId / 各区間のCameraId

| ID | 表示名 |
|---|---|
| `original` | 元のカメラ指示 |
| `fixed` | 構図を保つ |
| `push` / `pull` | ゆっくり寄る / ゆっくり引く |
| `arc-left` / `arc-right` | 左へ回り込む / 右へ回り込む |
| `front` / `three-quarter` | 正面へ回り込む / 斜め前へ回り込む |
| `track` | 被写体を追う |
| `pan-left` / `pan-right` | 左へパン / 右へパン |
| `truck-left` / `truck-right` | 左へ平行移動 / 右へ平行移動 |
| `tilt-up` / `tilt-down` | 上へ視線を移す / 下へ視線を移す |
| `eye-level` | 目線の高さへ移る |
| `low-angle` / `high-angle` | 低い位置から見上げる / 高い位置から見下ろす |
| `face` / `hands` / `upper-body` | 顔に寄る / 手元に寄る / 上半身に寄る |
| `full-body` | 全身を収める |
| `zoom-in` / `zoom-out` | その場からズームイン / その場からズームアウト |

これらは公式ガイドにあるカメラ動作と、目・眉・口などの具体的な描写を使うプリセットです。各項目の効き方や指定秒どおりの切替を全画像で実証したものではありません。画像の姿勢、遮蔽、主動作と両立する項目を選んでください。

## 15. 実装を確認するための参照先

上の説明と見本は、このファイルだけで編集できるように記載しています。以下は保守時の確認先です。製品の意味はproduct-contract、厳密な通信形式は対応するバージョン付き契約を正本にし、この資料を更新するときは実装との整合を再確認します。

| 対象 | 参照先 |
|---|---|
| 製品の動画・スタイル動作 | [product-contract.md](product-contract.md) のVideo generation節 |
| スタイル保存と未知フィールドの保持 | [MainWindow.LocalPersistence.cs](../local-native/PhotoViewer.Wpf/MainWindow.LocalPersistence.cs) |
| 外側のスタイル型 | [MainWindow.xaml.cs](../local-native/PhotoViewer.Wpf/MainWindow.xaml.cs) のVideoStyleState |
| 外側の値の読込・正規化 | [MainWindow.VideoGeneration.cs](../local-native/PhotoViewer.Wpf/MainWindow.VideoGeneration.cs) のNormalizeVideoStyle |
| 言語、Options、条件、直接展開・AI指示 | [VideoPromptProgram.cs](../local-native/PhotoViewer.Wpf/VideoPromptProgram.cs) |
| 画像種別、本文・ベースの選択 | [MainWindow.VideoPromptProgram.cs](../local-native/PhotoViewer.Wpf/MainWindow.VideoPromptProgram.cs) |
| 色付き選択と編集操作 | [VideoPromptAuthoringControl.cs](../local-native/PhotoViewer.Wpf/VideoPromptAuthoringControl.cs) |
| 演技カタログと優先順位 | [VideoSubjectDirection.cs](../local-native/PhotoViewer.Wpf/VideoSubjectDirection.cs) |
| 共通時間軸・撮り方・カメラワーク | [VideoDirectionTimeline.cs](../local-native/PhotoViewer.Wpf/VideoDirectionTimeline.cs) |
| 映像部分への挿入、引用・セリフの保護 | [VideoPromptSections.cs](../local-native/PhotoViewer.Wpf/VideoPromptSections.cs) |
| キュー追加と強化指示の確定 | [MainWindow.VideoSubmission.cs](../local-native/PhotoViewer.Wpf/MainWindow.VideoSubmission.cs) |
| H3の冒頭・セクション定義 | [MiniMaxH3I2vaPromptConformance.cs](../local-native/PhotoViewer.Wpf/MiniMaxH3I2vaPromptConformance.cs) |
| 実行直前のAI補完契約 | [enhancement-video-prompt-enhancement-v2.json](../contracts/enhancement-video-prompt-enhancement-v2.json) |
| 実際のLoRA適用契約 | [enhancement-video-lora-v1.json](../contracts/enhancement-video-lora-v1.json) |
| 選択言語の合成検証 | [verify-wpf-video-prompt-program.ps1](../scripts/verify-wpf-video-prompt-program.ps1) |

検証の区別: JSONとして有効、ネイティブ読込・展開が成功、LLMが意図どおり選択、実際の動画が意図を再現、は別の確認です。仕様書やパーサーの検証だけで最後の二つを証明することはできません。
