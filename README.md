# ClipInspect

ClipInspect は、Windows のクリップボードに実際に入っている形式と内容を、取得した時点の状態のまま調べる開発者向けの Windows 11 アプリです。コピーしたものが期待どおりに貼り付けられないときの原因調べに使い、結果を Markdown のレポートとして ChatGPT・Codex・Claude Code などに渡せます。表示は日本語のみです。

このリポジトリは、一般公開している ClipInspect のソースコードです。

## 主な機能

- ［取得］を押すと、クリップボードの形式を Windows が列挙した順に、ID・名前・種類・サイズ・状態とともに一覧にします。Windows が自動で作った形式の可能性（推定）も表示します
- テキスト（CF_UNICODETEXT・CF_TEXT・CF_OEMTEXT）の文字数・行数・改行の種類・終端の NUL。制御文字を記号で表示できます
- HTML Format のヘッダーの値とオフセットの整合性。HTML 全体・Fragment をソースのまま表示します（描画・実行はしません。RTF も同じ）
- 画像（CF_DIB・CF_DIBV5・CF_BITMAP・PNG）の寸法・ビット深度・上下の向き・圧縮・アルファとプレビュー
- ファイルの一覧（CF_HDROP）、独自形式の16進数、OLE・.NET（WPF）から見える形式、クリップボードの所有者、履歴・クラウド同期に含めない指定
- 概要・詳細の Markdown レポートのコピー。本文・パスなどは選んだときだけ含めます。コピーするときは、クリップボードの履歴とクラウド同期に含めない指定を付けます

クリップボードを読むのはボタンを押したときだけで、常駐・監視はしません。インターネットに接続せず、クリップボードの内容・取得結果・設定をファイルに保存しません（詳しくは下記のプライバシーポリシー）。

## 対応 OS と実行環境

- Windows 11（64 ビット、x64）
- Microsoft Store 版は .NET を内包しているため、追加のインストールは不要です。
- このリポジトリのソースからビルドして実行する場合は、[.NET 10 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/10.0) が必要です。

## インストール

正式な配布先は Microsoft Store です。

- Microsoft Store: https://apps.microsoft.com/detail/9N8345MCS8N6

この GitHub リポジトリでは実行ファイル（バイナリ）を配布していません。

## 基本的な使い方

1. 調べたいアプリでコピーします。
2. ClipInspect を起動し、［取得］を押します。起動しただけではクリップボードを読みません。
3. 左の形式一覧で形式を選ぶと、その内容を読み取って右側に表示します。［全形式を取得］ですべての形式をまとめて読みます。
4. レポートに内容を含めたい形式は「含める」にチェックを付けます。
5. ［概要レポートをコピー］、または［詳細レポートを確認…］で内容を確かめてから［コピー］を押し、AI などに貼り付けます。コピーすると、調べていたクリップボードの内容は置き換わります。

詳しい使い方は取扱説明書をご覧ください。

- 取扱説明書: https://www.ja1bus.com/clipinspect/manual/

## ビルド方法

### 必要な環境

- Windows 11 x64
- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)（確認済み: 10.0.404。ルートの `global.json` で 10.0 系の SDK を選びます）
- `dotnet` CLI だけでビルドできます（Visual Studio は不要）。
- 本体は NuGet パッケージに依存しません。テストだけが NuGet の `Microsoft.NET.Test.Sdk`・`xunit`・`xunit.runner.visualstudio` を使います（初回のビルド時に nuget.org から取得します）。

### コマンド

リポジトリのルートで実行します。

```powershell
dotnet build ClipInspect.csproj
dotnet test Tests/ClipInspect.Tests.csproj
dotnet run --project ClipInspect.csproj
```

実際のクリップボードを使う試験は `dotnet run --project NativeTests/ClipInspect.NativeTests.csproj` で実行します。**実行中のクリップボードの内容を上書きします。**

`LICENSE` と `THIRD-PARTY-NOTICES.md` は exe に埋め込まれ、［バージョン情報］から表示します（埋め込みには `public-repo/` の同じファイルを使います）。

### 構成

| パス | 内容 |
|---|---|
| `ClipInspect.csproj` | 本体（WPF、`net10.0-windows`） |
| `Core/` | 画面に依存しない処理: Win32 の P/Invoke（`Native/`）、取得と書き込み（`Capture/`）、テキスト・HTML・RTF・画像などの解析（`Analysis/`）、Markdown レポート（`Reports/`）、結果と上限（`Model/`） |
| `Services/`・`Views/` | OLE・.NET の形式の確認と画像のプレビュー、画面 |
| `Tests/` | 単体テスト（xUnit。偽の API を使い、実際のクリップボードには触れません） |
| `NativeTests/` | 実際のクリップボードを使う試験（明示的に実行） |
| `icon.png`・`Resources/icon.ico` | アプリアイコン（`pwsh -File tools/make-icon.ps1` で描き直します） |

Microsoft Store 版のパッケージ（MSIX）を作る仕組みは、このリポジトリに含めていません。

ソースコード中のコメントは日本語と英語が混在しています。

## ライセンス

[MIT License](LICENSE) で公開しています。

Copyright (c) 2026 ja1bus（© ja1bus）

第三者のソフトウェアについては [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。

## 無保証

本ソフトウェアは「現状のまま」提供され、明示・黙示を問わず、いかなる保証もありません。本ソフトウェアの使用によって生じたいかなる損害についても、作者は責任を負いません（詳細は [LICENSE](LICENSE)）。

## 関連リンク

- Web ページ: https://www.ja1bus.com/clipinspect/
- 取扱説明書: https://www.ja1bus.com/clipinspect/manual/
- プライバシーポリシー: https://www.ja1bus.com/clipinspect/privacy/
- Microsoft Store: https://apps.microsoft.com/detail/9N8345MCS8N6
- お問い合わせ: contact@ja1bus.com
