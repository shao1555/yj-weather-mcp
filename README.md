# yj-weather-mcp

Yahoo!天気（日本）から天気予報を取得する、ローカル用の MCP サーバー（stdio）。

> [!IMPORTANT]
> **非公式**のツールです。LINEヤフー株式会社とは一切関係がなく、承認や支援も受けていません。
> **無保証**です。取得した予報が正確である保証はありません。防災に関わる判断は気象庁や自治体の発表で確認してください。
> このツールの利用によって生じたいかなる損害についても、作者は責任を負いません。

## ツール

| ツール | 内容 |
|---|---|
| `get_forecast` | 市区町村のピンポイント天気。今日明日の 3 時間ごとの予報、地域の最高/最低気温と 6 時間ごとの降水確率、週間天気、生活指数、警報・注意報、AI 解説を返す |
| `get_prefecture_forecast` | 都道府県内の各地域の、指定日（今日〜7 日後）の天気・気温・降水確率と、府県天気概況を返す |
| `search_locations` | 市区町村名・レジャー施設名で地点を検索し、地点 ID を返す |

`get_forecast` の `location` には、市区町村名（`千代田区`）、地点 ID（`13/4410/13101`）、Yahoo!天気の地点ページ URL のどれでも渡せる。
地名に当てはまる地点が複数ある（`府中`、`港区` など）ときは、候補の地点 ID を列挙したエラーを返す。

## ビルド

```sh
mise install          # .NET SDK 10
mise run publish      # ./dist/yj-weather-mcp（自己完結型の単一バイナリ）を作る
```

`dist/yj-weather-mcp` は .NET ランタイムがなくても起動するので、MCP クライアントにはこのバイナリを登録する。
mise で入れた dotnet は `DOTNET_ROOT` を設定しないため、`bin/` 配下の apphost を直接指定すると起動しない。

## 登録

### Claude Code

```sh
claude mcp add yj-weather --scope user -- "$PWD/dist/yj-weather-mcp"
```

### Codex

```sh
codex mcp add yj-weather -- "$PWD/dist/yj-weather-mcp"
```

`codex mcp add` を使わない場合は、`~/.codex/config.toml` に直接書いてもよい。

```toml
[mcp_servers.yj-weather]
command = "/absolute/path/to/yj-weather-mcp/dist/yj-weather-mcp"
```

## しくみ

公開 API はないので、PC 版のページの HTML を解析している。

| 用途 | ページ |
|---|---|
| 地点の検索 | `GET /weather/search/?p={キーワード}` |
| ピンポイント天気（3 時間ごと・週間・指数・警報・AI 解説） | `GET /weather/jp/{都道府県}/{地域}/{市区町村 JIS コード}.html` |
| 地域の今日明日（最高/最低気温・降水確率・風・波） | `GET /weather/jp/{都道府県}/{地域}.html` |
| 都道府県の地域別予報と府県天気概況 | `GET /weather/jp/{都道府県}/?day={1-8}` |

都道府県コードは JIS の都道府県コードと同じ（`13` = 東京都）。北海道だけは `1a`（道北）・`1b`（道央）・`1c`（道東）・`1d`（道南）に分かれている。

同じページは 10 分間メモリにキャッシュし、同時リクエスト数は 4 に制限している。

## 注意

- サイトの HTML を解析しているので、サイトが改修されると動かなくなる可能性がある。
- 個人がローカルで使う前提で作っている。短い間隔で繰り返し問い合わせるような使い方はしないこと。
- 公式サイトの利用規約を守るのは利用者の責任とする。

## ライセンス

[MIT License](LICENSE)
