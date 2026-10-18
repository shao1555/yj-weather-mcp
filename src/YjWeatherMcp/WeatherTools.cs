using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace YjWeatherMcp;

[McpServerToolType]
public sealed class WeatherTools(WeatherClient client, ILogger<WeatherTools> logger)
{
    private static readonly CultureInfo JaCulture = CultureInfo.GetCultureInfo("ja-JP");

    [McpServerTool(Name = "search_locations", ReadOnly = true, Idempotent = true)]
    [Description("""
        Yahoo!天気で市区町村とレジャー施設（競技場・テーマパーク・ゴルフ場など）を名前で検索し、地点 ID（location_id）を返す。
        get_forecast の location に地名を渡して候補が複数あるとエラーになった場合に使う。
        駅名や番地を含む住所では見つからないので、市区町村名（例: 渋谷区, 府中市, 札幌市中央区）で検索すること。
        """)]
    public async Task<object> SearchLocations(
        [Description("市区町村名または施設名の一部（例: 渋谷, 府中, 東京ドーム）")] string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("query を指定してください。");
        var result = await client.SearchAsync(query.Trim(), ct);
        return new
        {
            cities = result.Cities.Select(c => new { location_id = c.Id, name = c.Name }),
            facilities = result.Facilities.Select(f => new { name = f.Name, category = f.Category, location_id = f.City.Id, city = f.City.Name }),
            note = result.Cities.Count == 0 && result.Facilities.Count == 0
                ? "一致する地点がありません。市区町村名で検索してください。"
                : "facilities は先頭 10 件まで。施設の天気は所在する市区町村（location_id）のピンポイント天気で見る。",
        };
    }

    [McpServerTool(Name = "get_forecast", ReadOnly = true, Idempotent = true)]
    [Description("""
        市区町村のピンポイント天気を Yahoo!天気から取得する。内容:
        - 今日・明日: 天気、最高/最低気温（前日差）、6 時間ごとの降水確率、風・波（以上は地域単位の予報）と、
          3 時間ごとの天気・気温・湿度・降水量・風向風速（市区町村単位。過ぎた時刻は省く）、生活指数（洗濯・傘・紫外線など）
        - 明後日から 6 日分の週間天気（天気、最高/最低気温、降水確率）
        - 発表中の警報・注意報、AI による解説
        結果を利用者に示すときは url（Yahoo!天気のページ）をリンクとして添えること。
        """)]
    public async Task<object> GetForecast(
        [Description("地点。市区町村名（例: 千代田区, 横浜市西区）、search_locations の location_id（例: 13/4410/13101）、または Yahoo!天気の地点ページ URL")] string location,
        [Description("false なら 3 時間ごとの予報を省いて返す")] bool include_hourly = true,
        [Description("false なら生活指数を省いて返す")] bool include_indices = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(location)) throw new McpException("location を指定してください。");
        var loc = await client.ResolveAsync(location, ct);

        var pointTask = client.GetPointAsync(loc, ct);
        var areaTask = GetAreaDaysOrEmptyAsync(loc, ct);
        var point = await pointTask;
        var areaDays = (await areaTask).ToDictionary(d => d.Date);

        var days = point.Days.Select(d =>
        {
            areaDays.TryGetValue(d.Date, out var a);
            point.Indices.TryGetValue(d.Date, out var idx);
            return new
            {
                date = FormatDate(d.Date),
                weather = a?.Weather,
                high = a?.High,
                high_diff = a?.HighDiff,
                low = a?.Low,
                low_diff = a?.LowDiff,
                precip_probability = a?.PrecipProbability.ToDictionary(s => s.Slot + "時", s => s.Percent),
                wind = a?.Wind,
                wave = a?.Wave,
                hourly = include_hourly
                    ? d.Hourly.Where(h => !h.Past).Select(h => new
                    {
                        time = $"{h.Hour:00}:00",
                        weather = h.Weather,
                        temp = h.Temperature,
                        humidity = h.Humidity,
                        precip_mm = h.PrecipitationMm,
                        wind_dir = h.WindDirection,
                        wind_mps = h.WindSpeed,
                    })
                    : null,
                indices = include_indices && idx is not null
                    ? idx.Select(i => new { name = i.Name, value = i.Value, text = i.Text })
                    : null,
            };
        });

        return new
        {
            location = new
            {
                location_id = loc.Id,
                name = point.Name,
                prefecture = point.Prefecture,
                area = point.Area,
                url = loc.Url,
            },
            announced_at = point.AnnouncedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ai_summary = point.AiSummary,
            warnings = new
            {
                issued = point.Warnings.Groups.Select(g => new { level = g.Level, items = g.Items }),
                url = point.Warnings.Url,
            },
            days,
            weekly = point.Weekly.Select(w => new
            {
                date = FormatDate(w.Date),
                weather = w.Weather,
                high = w.High,
                low = w.Low,
                precip_probability = w.PrecipProbability,
            }),
            note = "気温は ℃、降水確率は %、降水量は mm（3 時間）、風速は m/s。今日明日の天気・最高最低気温・降水確率は"
                + (point.Area is null ? "地域" : $"「{point.Area}」地域")
                + "の予報で、hourly だけが市区町村単位。warnings.issued が空なら警報・注意報は発表されていない。",
        };
    }

    [McpServerTool(Name = "get_prefecture_forecast", ReadOnly = true, Idempotent = true)]
    [Description("""
        都道府県内の各地域（例: 東京都なら 東京・大島・八丈島・父島）について、指定日の天気・最高/最低気温・降水確率を Yahoo!天気から取得する。
        今日から 7 日後までを指定できる。都道府県全体の傾向をざっと見たいときや、旅行先の数日分を比べたいときに使う。
        気象台の府県天気概況（文章）も返す。
        """)]
    public async Task<object> GetPrefectureForecast(
        [Description("都道府県（例: 東京都, 大阪, 13）。北海道は 道北 / 道央 / 道東 / 道南 のいずれかで指定する")] string prefecture,
        [Description("日付 (YYYY-MM-DD)。省略時は今日")] string? date = null,
        CancellationToken ct = default)
    {
        var pref = Prefectures.Find(prefecture)
            ?? throw new McpException(prefecture.Trim() is "北海道" or "北海道地方"
                ? "北海道は 道北 / 道央 / 道東 / 道南 のいずれかで指定してください（札幌は道央）。"
                : $"都道府県「{prefecture}」が見つかりません。例: 東京都, 大阪府, 沖縄");

        var today = DateOnly.FromDateTime(WeatherClient.NowJst());
        var day = 1;
        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                throw new McpException("date は YYYY-MM-DD 形式で指定してください。");
            day = d.DayNumber - today.DayNumber + 1;
            if (day is < 1 or > 8)
                throw new McpException($"date は今日（{today:yyyy-MM-dd}）から 7 日後までを指定してください。");
        }

        var page = await client.GetPrefectureAsync(pref.Code, day, ct);
        return new
        {
            prefecture = page.Name,
            date = page.Date is { } sd ? FormatDate(sd) : null,
            announced_at = page.AnnouncedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            areas = page.Areas.Select(a => new
            {
                name = a.Name,
                weather = a.Weather,
                high = a.High,
                low = a.Low,
                precip_probability = a.PrecipProbability,
                url = a.Url,
            }),
            overview = page.Overview,
            url = $"https://weather.yahoo.co.jp/weather/jp/{pref.Code}/?day={day}",
            note = "気温は ℃、降水確率は その日の最大値（%）。市区町村ごとの詳しい予報は get_forecast で取得する。",
        };
    }

    private async Task<IReadOnlyList<AreaDay>> GetAreaDaysOrEmptyAsync(Location loc, CancellationToken ct)
    {
        try
        {
            return await client.GetAreaDaysAsync(loc, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 地域の予報は補足情報なので、取れなくてもピンポイント天気だけは返す
            logger.LogWarning(ex, "{Location} の地域予報の取得に失敗", loc.Id);
            return [];
        }
    }

    private static string FormatDate(DateOnly d) => d.ToString("yyyy-MM-dd (ddd)", JaCulture);
}
