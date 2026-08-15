namespace PingWatcher.Core.Survey;

/// <summary>
/// 無線サーベイ 1 回ぶんの記録（surveys フォルダの 1 ファイル）。
///
/// 測定点の座標は 0..1 の正規化座標で持つ。表示解像度や DPI に依存させないためで、
/// フロア図の実寸は <see cref="AspectRatio"/> だけが決める。フロア図の画像そのものは
/// JSON に埋め込まず、<b>JSON と同じフォルダに置いたファイル名</b>で参照する
/// （USB で持ち出す前提なので絶対パスは使えない。フォルダごとコピーすれば成立する）。
/// </summary>
public sealed class SurveyDocument
{
    /// <summary>将来フォーマットを変えたときの移行判断用。</summary>
    public int Version { get; set; } = 1;

    public string Name { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>フロア図のファイル名（JSON と同じフォルダ）。null なら方眼。</summary>
    public string? FloorImageFile { get; set; }

    /// <summary>フロア図の縦横比（幅/高さ）。方眼は 4:3。</summary>
    public double AspectRatio { get; set; } = 4.0 / 3.0;

    public List<SurveyPoint> Points { get; set; } = [];
}

/// <summary>測定点 1 つ。クリックした位置と、その場で見えた AP の一覧。</summary>
public sealed class SurveyPoint
{
    /// <summary>0..1 の正規化座標（フロア図の左上が原点）。</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public DateTimeOffset MeasuredAt { get; set; }

    /// <summary>測定時に接続していた AP の BSSID。未接続なら null。</summary>
    public string? ConnectedBssid { get; set; }

    public List<SurveyReading> Readings { get; set; } = [];
}

/// <summary>測定点で見えた AP 1 台ぶんの実測値。</summary>
public sealed class SurveyReading
{
    public string Ssid { get; set; } = "";

    public string Bssid { get; set; } = "";

    /// <summary>受信強度（dBm、負の値）。</summary>
    public int Rssi { get; set; }

    public int Channel { get; set; }

    public float Band { get; set; }
}
