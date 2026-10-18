using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace YjWeatherMcp;

/// <summary>
/// Yahoo!天気（weather.yahoo.co.jp）の HTML ページを取得して解析するクライアント。
/// 予報の発表は数時間おきなので、同じページは短時間キャッシュして問い合わせを減らす。
/// </summary>
public sealed partial class WeatherClient(IHttpClientFactory httpClientFactory, ILogger<WeatherClient> logger)
{
    public const string HttpClientName = "yahoo-weather";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim Throttle = new(4);
    private static readonly HtmlParser Parser = new();
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private readonly ConcurrentDictionary<string, (DateTime FetchedAt, string Html)> _cache = new();

    public static DateTime NowJst() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Jst).DateTime;

    // ---- 検索 ----

    /// <summary>サイト内検索。市区町村名・レジャー施設名に部分一致する。駅名や住所の番地には対応していない。</summary>
    public async Task<SearchResult> SearchAsync(string query, CancellationToken ct)
    {
        var html = await GetStringAsync($"weather/search/?p={Uri.EscapeDataString(query)}", ct);
        return ParseSearch(html);
    }

    internal static SearchResult ParseSearch(string html)
    {
        var doc = Parser.ParseDocument(html);
        var cities = doc.QuerySelectorAll("#rsltmuni table tr")
            .Select(tr => tr.QuerySelector("a[href]"))
            .Select(a => a is null ? null : ToLocation(a.GetAttribute("href"), Clean(a.TextContent)))
            .OfType<Location>()
            .ToList();
        var facilities = doc.QuerySelectorAll("#rsltleis table tr")
            .Select(tr => tr.QuerySelectorAll("td").ToList())
            .Where(tds => tds.Count >= 3)
            .Select(tds =>
            {
                var a = tds[1].QuerySelector("a[href]");
                var city = a is null ? null : ToLocation(a.GetAttribute("href"), Clean(a.TextContent));
                return city is null ? null : new Facility(Clean(tds[0].TextContent), Clean(tds[2].TextContent), city);
            })
            .OfType<Facility>()
            .ToList();
        return new SearchResult(cities, facilities);
    }

    /// <summary>
    /// 地点 ID・Yahoo!天気の URL・地名のいずれかから地点を決める。
    /// 地名で候補が複数あるときは候補を列挙した McpException を投げる。
    /// </summary>
    public async Task<Location> ResolveAsync(string location, CancellationToken ct)
    {
        var q = location.Trim();
        var m = LocationIdRegex().Match(q);
        if (m.Success) return new Location($"{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}", q);

        var result = await SearchAsync(q, ct);
        var cities = result.Cities;
        if (cities.Count == 0)
        {
            // 市区町村で見つからなければ、レジャー施設の所在地を使う
            var byCity = result.Facilities.GroupBy(f => f.City.Id).ToList();
            if (byCity.Count == 0)
                throw new McpException($"「{q}」に一致する市区町村・施設が見つかりません。市区町村名（例: 渋谷区, 府中市）で指定してください。");
            if (byCity.Count == 1) return byCity[0].First().City;
            if (result.Facilities.FirstOrDefault(f => f.Name == q) is { } exactFacility) return exactFacility.City;
            throw new McpException($"「{q}」に一致する施設が複数の市区町村にあります。location に地点 ID を指定してください: " +
                string.Join(", ", byCity.Select(g => $"{g.Key}={g.First().City.Name}（{string.Join("、", g.Select(f => f.Name).Take(3))}）")));
        }
        if (cities.Count == 1) return cities[0];

        // 「港区」で「東京都港区」と「愛知県名古屋市港区」が出るような場合は、末尾一致・完全一致を優先する
        var exact = cities.Where(c => c.Name == q || c.Name.EndsWith(q, StringComparison.Ordinal)).ToList();
        if (exact.Count == 1) return exact[0];

        throw new McpException($"「{q}」に一致する地点が複数あります。location に地点 ID を指定してください: " +
            string.Join(", ", cities.Take(20).Select(c => $"{c.Id}={c.Name}")));
    }

    // ---- ピンポイント天気（市区町村） ----

    public async Task<PointPage> GetPointAsync(Location loc, CancellationToken ct)
    {
        var html = await GetStringAsync($"weather/jp/{loc.Id}.html", ct);
        return ParsePoint(html);
    }

    internal static PointPage ParsePoint(string html)
    {
        var doc = Parser.ParseDocument(html);
        var name = TitleName(doc) ?? "";
        var announced = ParseAnnounced(doc.QuerySelector("#yjw_pinpoint .yjw_note_h2")?.TextContent);
        var baseDate = DateOnly.FromDateTime(announced ?? NowJst());

        // パンくず: 都道府県ページと地域ページへのリンク
        string? prefecture = null, area = null;
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href") ?? "";
            if (prefecture is null && PrefLinkRegex().IsMatch(href)) prefecture = Clean(a.TextContent);
            else if (area is null && AreaLinkRegex().IsMatch(href)) area = Clean(a.TextContent);
        }

        var days = new List<PointDay>();
        foreach (var id in new[] { "yjw_pinpoint_today", "yjw_pinpoint_tomorrow" })
        {
            var box = doc.GetElementById(id);
            var date = ParseMonthDay(box?.QuerySelector("h3")?.TextContent, baseDate);
            var table = box?.QuerySelector("table");
            if (date is null || table is null) continue;
            days.Add(new PointDay(date.Value, ParseHourlyTable(table)));
        }

        var warnGroups = doc.QuerySelectorAll("#wrnrpt .warnAdv_area dl")
            .Select(dl => new WarningGroup(
                Clean(dl.QuerySelector("dt")?.TextContent),
                dl.QuerySelectorAll("dd")
                    .SelectMany(dd => Clean(dd.TextContent).Split([' ', '、', ','], StringSplitOptions.RemoveEmptyEntries))
                    .ToList()))
            .Where(g => g.Level.Length > 0 && g.Level != "発表なし")
            .ToList();
        var warnUrl = doc.QuerySelector("#wrnrpt .warnAdv_area a[href]")?.GetAttribute("href") ?? "https://typhoon.yahoo.co.jp/weather/jp/warn/";

        var ai = doc.QuerySelector("#ai_overview .pptWeather_aiExplanation_detail")?.TextContent;

        return new PointPage(
            name, prefecture, area, announced,
            string.IsNullOrWhiteSpace(ai) ? null : Clean(ai),
            new Warnings(warnGroups, warnUrl),
            days,
            ParseWeekly(doc, baseDate),
            ParseIndices(doc, baseDate));
    }

    private static List<HourlyForecast> ParseHourlyTable(IElement table)
    {
        var rows = table.QuerySelectorAll("tr")
            .Select(tr => tr.Children.Where(c => c.LocalName is "td" or "th").ToList())
            .Where(cells => cells.Count > 1)
            .ToDictionary(cells => Clean(cells[0].TextContent), cells => cells.Skip(1).ToList());
        if (!rows.TryGetValue("時刻", out var times)) return [];

        List<IElement>? Row(string prefix) => rows.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Value;
        var weather = Row("天気");
        var temp = Row("気温");
        var humid = Row("湿度");
        var precip = Row("降水量");
        var wind = Row("風向風速");

        string? Cell(List<IElement>? row, int i) => row is not null && i < row.Count ? Clean(row[i].TextContent) : null;

        return times.Select((t, i) =>
        {
            var windParts = (wind is not null && i < wind.Count ? wind[i].TextContent : "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return new HourlyForecast(
                Hour: ParseInt(Clean(t.TextContent).TrimEnd('時')) ?? i * 3,
                // 過ぎた時刻は灰色の文字で表示される
                Past: t.QuerySelector("font[color]") is not null,
                Weather: NullIfEmpty(Cell(weather, i)),
                Temperature: ParseDouble(Cell(temp, i)),
                Humidity: ParseInt(Cell(humid, i)),
                PrecipitationMm: ParseDouble(Cell(precip, i)),
                WindDirection: windParts.Length > 0 ? windParts[0] : null,
                WindSpeed: windParts.Length > 1 ? ParseDouble(windParts[1]) : null);
        }).ToList();
    }

    private static List<WeeklyDay> ParseWeekly(IDocument doc, DateOnly baseDate)
    {
        var rows = doc.QuerySelectorAll("#yjw_week table tr")
            .Select(tr => tr.Children.Where(c => c.LocalName == "td").ToList())
            .Where(cells => cells.Count > 1)
            .ToList();
        List<IElement>? Row(string prefix) => rows.FirstOrDefault(r => Clean(r[0].TextContent).StartsWith(prefix, StringComparison.Ordinal))?.Skip(1).ToList();
        var dates = Row("日付");
        if (dates is null) return [];
        var weather = Row("天気");
        var temp = Row("気温");
        var pop = Row("降水確率");

        var result = new List<WeeklyDay>();
        for (var i = 0; i < dates.Count; i++)
        {
            var date = ParseMonthDay(dates[i].TextContent, baseDate);
            if (date is null) continue;
            var temps = temp is not null && i < temp.Count ? temp[i].QuerySelectorAll("font").Select(f => ParseInt(Clean(f.TextContent))).ToList() : [];
            result.Add(new WeeklyDay(
                date.Value,
                weather is not null && i < weather.Count ? NullIfEmpty(Clean(weather[i].TextContent)) : null,
                temps.Count > 0 ? temps[0] : null,
                temps.Count > 1 ? temps[1] : null,
                pop is not null && i < pop.Count ? ParseInt(Clean(pop[i].TextContent)) : null));
        }
        return result;
    }

    private static Dictionary<DateOnly, IReadOnlyList<WeatherIndex>> ParseIndices(IDocument doc, DateOnly baseDate)
    {
        var result = new Dictionary<DateOnly, IReadOnlyList<WeatherIndex>>();
        var tabs = doc.QuerySelectorAll(".indexList .tabView_navi a[href^='#']").ToList();
        foreach (var tab in tabs)
        {
            var date = ParseMonthDay(tab.TextContent, baseDate);
            var content = doc.GetElementById(tab.GetAttribute("href")![1..]);
            if (date is null || content is null) continue;
            result[date.Value] = content.QuerySelectorAll("dl.indexList_item")
                .Select(dl =>
                {
                    var valueClass = dl.QuerySelector(".index_value")?.ClassList.FirstOrDefault(c => c.StartsWith("index_value-", StringComparison.Ordinal));
                    return new WeatherIndex(
                        Clean(dl.QuerySelector("dt")?.TextContent),
                        valueClass is null ? null : ParseInt(valueClass["index_value-".Length..]),
                        NullIfEmpty(Clean(dl.QuerySelector(".index_text")?.TextContent)));
                })
                .ToList();
        }
        return result;
    }

    // ---- 地域（一次細分区域）の今日明日 ----

    public async Task<IReadOnlyList<AreaDay>> GetAreaDaysAsync(Location loc, CancellationToken ct)
    {
        var html = await GetStringAsync($"weather/jp/{loc.PrefCode}/{loc.AreaCode}.html", ct);
        return ParseAreaDays(html);
    }

    internal static IReadOnlyList<AreaDay> ParseAreaDays(string html)
    {
        var doc = Parser.ParseDocument(html);
        var announced = ParseAnnounced(doc.QuerySelector(".yjw_note_h2")?.TextContent);
        var baseDate = DateOnly.FromDateTime(announced ?? NowJst());
        var result = new List<AreaDay>();
        foreach (var div in doc.QuerySelectorAll(".forecastCity > table > tbody > tr > td > div, .forecastCity > table > tr > td > div"))
        {
            var date = ParseMonthDay(div.QuerySelector("p.date")?.TextContent, baseDate);
            if (date is null) continue;
            var (high, highDiff) = ParseTemp(div.QuerySelector("li.high"));
            var (low, lowDiff) = ParseTemp(div.QuerySelector("li.low"));
            var slots = div.QuerySelectorAll("tr.time td").Select(td => Clean(td.TextContent)).ToList();
            var pops = div.QuerySelectorAll("tr.precip td").Select(td => ParseInt(Clean(td.TextContent))).ToList();
            string? Dd(string label) => div.QuerySelectorAll("dl dt")
                .FirstOrDefault(dt => Clean(dt.TextContent).StartsWith(label, StringComparison.Ordinal))?
                .NextElementSibling is { LocalName: "dd" } dd ? NullIfEmpty(Clean(dd.TextContent).Trim('-')) : null;
            result.Add(new AreaDay(
                date.Value,
                NullIfEmpty(Clean(div.QuerySelector("p.pict")?.TextContent)),
                high, highDiff, low, lowDiff,
                slots.Select((s, i) => new PrecipSlot(s, i < pops.Count ? pops[i] : null)).ToList(),
                Dd("風"),
                Dd("波")));
        }
        return result;
    }

    private static (int?, string?) ParseTemp(IElement? li)
    {
        if (li is null) return (null, null);
        var value = ParseInt(Clean(li.QuerySelector("em")?.TextContent));
        var m = TempDiffRegex().Match(li.TextContent);
        return (value, m.Success ? m.Groups[1].Value : null);
    }

    // ---- 都道府県 ----

    /// <param name="day">1 = 今日 … 8 = 7 日後</param>
    public async Task<PrefecturePage> GetPrefectureAsync(string prefCode, int day, CancellationToken ct)
    {
        var html = await GetStringAsync($"weather/jp/{prefCode}/?day={day}", ct);
        return ParsePrefecture(html, prefCode);
    }

    internal static PrefecturePage ParsePrefecture(string html, string prefCode)
    {
        var doc = Parser.ParseDocument(html);
        var name = Clean(doc.QuerySelector("#navHeader .title")?.TextContent).Replace("の天気", "");
        var announced = ParseAnnounced(doc.QuerySelector("#navHeader .time")?.TextContent);
        var now = announced ?? NowJst();

        // カレンダーは「19(月)」のように日しか書かれていないので、発表日から順に日付を割り当てる
        var dates = new List<DateOnly>();
        DateOnly? selected = null;
        var cursor = DateOnly.FromDateTime(now);
        foreach (var li in doc.QuerySelectorAll("#navCal li"))
        {
            var d = ParseInt(Clean(li.QuerySelector("em")?.TextContent));
            if (d is null) continue;
            while (cursor.Day != d) cursor = cursor.AddDays(1);
            dates.Add(cursor);
            if (li.QuerySelector("a") is null) selected = cursor;
            cursor = cursor.AddDays(1);
        }

        var areas = doc.QuerySelectorAll("#map li.point")
            .Select(li =>
            {
                var href = li.QuerySelector("a[href]")?.GetAttribute("href") ?? "";
                var m = AreaLinkRegex().Match(href);
                var temps = Clean(li.QuerySelector(".temp")?.TextContent).Split('/');
                return new AreaSummary(
                    AreaCode: m.Success ? m.Groups[2].Value : "",
                    Name: Clean(li.QuerySelector(".name")?.TextContent),
                    Weather: NullIfEmpty(li.QuerySelector(".icon img")?.GetAttribute("alt")),
                    High: temps.Length > 0 ? ParseInt(temps[0]) : null,
                    Low: temps.Length > 1 ? ParseInt(temps[1]) : null,
                    PrecipProbability: ParseInt(Clean(li.QuerySelector(".precip")?.TextContent)),
                    Url: new Uri(new Uri("https://weather.yahoo.co.jp/"), href).ToString());
            })
            .Where(a => a.Name.Length > 0)
            .ToList();

        var overview = doc.QuerySelector("#condition .text");
        return new PrefecturePage(
            name.Length > 0 ? name : prefCode, announced, dates, selected,
            overview is null ? null : TextWithLineBreaks(overview),
            areas);
    }

    /// <summary>&lt;br&gt; を改行として残しつつテキストを取り出す。</summary>
    private static string TextWithLineBreaks(IElement el)
    {
        var lines = new List<string> { "" };
        foreach (var node in el.ChildNodes)
        {
            if (node is IElement { LocalName: "br" }) lines.Add("");
            else lines[^1] += node.TextContent;
        }
        return string.Join("\n", lines.Select(Clean).Where(l => l.Length > 0));
    }

    // ---- 共通 ----

    private async Task<string> GetStringAsync(string path, CancellationToken ct)
    {
        if (_cache.TryGetValue(path, out var hit) && DateTime.UtcNow - hit.FetchedAt < CacheTtl) return hit.Html;

        await Throttle.WaitAsync(ct);
        try
        {
            using var res = await httpClientFactory.CreateClient(HttpClientName).GetAsync(path, ct);
            if (res.StatusCode == HttpStatusCode.NotFound)
                throw new McpException($"ページが見つかりません: https://weather.yahoo.co.jp/{path}（地点 ID が誤っている可能性があります）");
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("{Path} が {Status} を返しました", path, (int)res.StatusCode);
                throw new McpException($"Yahoo!天気への問い合わせに失敗しました（HTTP {(int)res.StatusCode}）。");
            }
            var html = await res.Content.ReadAsStringAsync(ct);
            _cache[path] = (DateTime.UtcNow, html);
            return html;
        }
        finally
        {
            Throttle.Release();
        }
    }

    private static Location? ToLocation(string? href, string name)
    {
        if (href is null) return null;
        // 検索結果は /weather/13/4410/13101.html、地点ページは /weather/jp/13/4410/13101.html
        var m = LocationIdRegex().Match(href);
        return m.Success ? new Location($"{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}", name) : null;
    }

    private static string? TitleName(IDocument doc)
    {
        var t = doc.Title ?? "";
        var i = t.IndexOf("の天気", StringComparison.Ordinal);
        return i > 0 ? t[..i] : null;
    }

    private static DateTime? ParseAnnounced(string? text)
    {
        if (text is null) return null;
        var m = AnnouncedRegex().Match(text);
        if (!m.Success) return null;
        var now = NowJst();
        var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var year = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : InferYear(DateOnly.FromDateTime(now), month, day);
        return new DateTime(year, month, day, int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture), 0);
    }

    /// <summary>「10月19日」のような年なしの日付を、基準日に最も近い年の日付にする。</summary>
    private static DateOnly? ParseMonthDay(string? text, DateOnly baseDate)
    {
        if (text is null) return null;
        var m = MonthDayRegex().Match(text);
        if (!m.Success) return null;
        var month = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return new DateOnly(InferYear(baseDate, month, day), month, day);
    }

    private static int InferYear(DateOnly baseDate, int month, int day) =>
        new[] { baseDate.Year - 1, baseDate.Year, baseDate.Year + 1 }
            .Where(y => day <= DateTime.DaysInMonth(y, month))
            .MinBy(y => Math.Abs(new DateOnly(y, month, day).DayNumber - baseDate.DayNumber));

    internal static string Clean(string? s) =>
        s is null ? "" : WhitespaceRegex().Replace(s.Replace('　', ' '), " ").Trim();

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static int? ParseInt(string? s)
    {
        var digits = s is null ? "" : SignedNumberRegex().Match(s).Value;
        return int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? ParseDouble(string? s)
    {
        var num = s is null ? "" : DecimalRegex().Match(s).Value;
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [GeneratedRegex(@"(?:^|/weather/(?:jp/)?)(\d{1,2}[a-d]?)/(\d{4})/(\d{4,5})(?:\.html)?/?$")]
    private static partial Regex LocationIdRegex();

    [GeneratedRegex(@"/weather/jp/(\d{1,2}[a-d]?)/?$")]
    private static partial Regex PrefLinkRegex();

    [GeneratedRegex(@"/weather/jp/(\d{1,2}[a-d]?)/(\d{4})\.html$")]
    private static partial Regex AreaLinkRegex();

    [GeneratedRegex(@"(?:(\d{4})年)?(\d{1,2})月(\d{1,2})日\s*(\d{1,2})時(\d{1,2})分")]
    private static partial Regex AnnouncedRegex();

    [GeneratedRegex(@"(\d{1,2})月\s*(\d{1,2})日")]
    private static partial Regex MonthDayRegex();

    [GeneratedRegex(@"\[([+\-−]?\d+)\]")]
    private static partial Regex TempDiffRegex();

    [GeneratedRegex(@"[+-]?\d+")]
    private static partial Regex SignedNumberRegex();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex DecimalRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
