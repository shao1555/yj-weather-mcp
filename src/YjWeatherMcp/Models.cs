namespace YjWeatherMcp;

/// <summary>ピンポイント天気の地点。Id は URL の "{都道府県コード}/{地域コード}/{市区町村 JIS コード}"（例: 13/4410/13101）。</summary>
public sealed record Location(string Id, string Name)
{
    public string PrefCode => Id.Split('/')[0];
    public string AreaCode => Id.Split('/')[1];
    public string JisCode => Id.Split('/')[2];
    public string Url => $"https://weather.yahoo.co.jp/weather/jp/{Id}.html";
    public string AreaUrl => $"https://weather.yahoo.co.jp/weather/jp/{PrefCode}/{AreaCode}.html";
}

public sealed record SearchResult(IReadOnlyList<Location> Cities, IReadOnlyList<Facility> Facilities);

/// <summary>検索結果のレジャー施設。天気は所在する市区町村のピンポイント天気を使う。</summary>
public sealed record Facility(string Name, string Category, Location City);

/// <summary>ピンポイント天気ページ（市区町村）の内容。</summary>
public sealed record PointPage(
    string Name,
    string? Prefecture,
    string? Area,
    DateTime? AnnouncedAt,
    string? AiSummary,
    Warnings Warnings,
    IReadOnlyList<PointDay> Days,
    IReadOnlyList<WeeklyDay> Weekly,
    IReadOnlyDictionary<DateOnly, IReadOnlyList<WeatherIndex>> Indices);

public sealed record Warnings(IReadOnlyList<WarningGroup> Groups, string Url);

/// <summary>Level は「警報」「注意報」など、Items は「大雨」「雷」など。</summary>
public sealed record WarningGroup(string Level, IReadOnlyList<string> Items);

public sealed record PointDay(DateOnly Date, IReadOnlyList<HourlyForecast> Hourly);

public sealed record HourlyForecast(
    int Hour,
    bool Past,
    string? Weather,
    double? Temperature,
    int? Humidity,
    double? PrecipitationMm,
    string? WindDirection,
    double? WindSpeed);

public sealed record WeeklyDay(DateOnly Date, string? Weather, int? High, int? Low, int? PrecipProbability);

public sealed record WeatherIndex(string Name, int? Value, string? Text);

/// <summary>地域（一次細分区域）の今日明日の天気。</summary>
public sealed record AreaDay(
    DateOnly Date,
    string? Weather,
    int? High,
    string? HighDiff,
    int? Low,
    string? LowDiff,
    IReadOnlyList<PrecipSlot> PrecipProbability,
    string? Wind,
    string? Wave);

/// <summary>Slot は "0-6" のような時間帯、Percent は過ぎた時間帯なら null。</summary>
public sealed record PrecipSlot(string Slot, int? Percent);

/// <summary>都道府県ページの 1 日分。</summary>
public sealed record PrefecturePage(
    string Name,
    DateTime? AnnouncedAt,
    IReadOnlyList<DateOnly> AvailableDates,
    DateOnly? Date,
    string? Overview,
    IReadOnlyList<AreaSummary> Areas);

public sealed record AreaSummary(string AreaCode, string Name, string? Weather, int? High, int? Low, int? PrecipProbability, string Url);
