using System.Windows;

namespace NetworkToys.App;

/// <summary>
/// 文字の大きさ。
///
/// 仕組みは配色（<see cref="ThemeManager"/>）と同じ考え方だが、辞書は差し替えない。
/// <b><see cref="Application.Resources"/> に計算した値を直接入れる</b>だけ。
/// 画面側は寸法をすべて <c>DynamicResource</c> で引いているので
/// （<c>Size.*</c> の参照 117 件はすべて動的解決。<c>StaticResource</c> はゼロ）、
/// 入れた瞬間に全タブ・メニュー・ツールチップ・別窓まで反映される。
///
/// <b><c>Tokens.xaml</c> の値は書き換えない。</b>あちらが「標準」の定義で、
/// 起動時に 1 度読んで基準として持つ（二重管理にしない）。
/// </summary>
internal static class UiScale
{
    /// <summary>倍率を掛ける対象。<c>Tokens.xaml</c> にあるキーと 1 対 1。</summary>
    private static readonly string[] Keys =
    [
        "Size.Title", "Size.Heading", "Size.Body", "Size.Caption", "Size.Micro",
        "Size.RowHeight", "Size.RowHeight.Wide", "Size.RowHeight.Input", "Size.LineHeight",
    ];

    /// <summary>標準（<c>Tokens.xaml</c> に書いてある値）。起動時に 1 度だけ読む。</summary>
    private static readonly Dictionary<string, double> Base = [];

    /// <summary>
    /// 選べる段階。<b>標準はいまの見た目そのもの</b>なので 1.0。
    /// 「小」は 1 画面に入る行数を増やしたいとき用。
    /// </summary>
    public static IReadOnlyList<(string Name, double Value)> Steps =>
    [
        ("小", 0.85),
        ("標準", 1.0),
        ("大", 1.25),
        ("特大", 1.5),
    ];

    /// <summary>いまの倍率。</summary>
    public static double Current { get; private set; } = 1.0;

    /// <summary>
    /// 倍率が変わった後に発火する。列幅を比例させるのに使う
    /// （文字だけ大きくすると、固定幅の列で文字が切れる）。
    /// </summary>
    /// <remarks>引数は「前の倍率に対する比」。いまの列幅にそのまま掛ければよい。</remarks>
    public static event Action<double>? Changed;

    /// <summary>
    /// 保存された倍率を読んで適用する。
    /// <b><see cref="Settings.Initialize"/> の後、ウィンドウを作る前に呼ぶこと</b>
    /// （後から変えると一瞬だけ標準の大きさで描かれる）。
    /// </summary>
    public static void Initialize()
    {
        Remember();

        double scale = Normalize(Settings.Current.UiScale);

        Current = scale;

        // 標準なら Tokens.xaml のままでよい。触らないのがいちばん確実
        if (scale != 1.0) Write(scale);
    }

    /// <summary>切り替える。保存もする。</summary>
    public static void Apply(double scale)
    {
        scale = Normalize(scale);

        if (scale == Current) return;

        double ratio = scale / Current;

        Current = scale;
        Write(scale);
        Save(scale);

        Changed?.Invoke(ratio);
    }

    /// <summary>いまの倍率がその段階か。メニューのチェックに使う。</summary>
    public static bool Is(double scale) => Math.Abs(Current - scale) < 0.001;

    /// <summary>
    /// <c>Tokens.xaml</c> の値を基準として控える。
    /// <b>1 度だけ</b> — 上書きした後に読むと、それが基準になってしまう。
    /// </summary>
    private static void Remember()
    {
        if (Base.Count > 0) return;

        foreach (string key in Keys)
        {
            if (Application.Current?.TryFindResource(key) is double value)
                Base[key] = value;
        }
    }

    private static void Write(double scale)
    {
        if (Application.Current is not { } app) return;

        foreach ((string key, double value) in Base)
        {
            // 半端な小数は文字の輪郭を鈍らせるので整数に丸める。
            // 1px でも残さないと消える（Micro の 9 は 0.85 倍で 7.65）
            app.Resources[key] = Math.Max(1, Math.Round(value * scale));
        }

        // ツールチップの幅は「全角 68 字 + 余白」。数を直接持つと倍率で崩れるので、
        // 本文の大きさから毎回出す（CLAUDE.md の 68 字の決まりが倍率に依らず成り立つ）
        if (Base.TryGetValue("Size.Body", out double body))
            app.Resources["Size.TooltipWidth"] = Math.Round(body * scale) * 68 + 24;
    }

    /// <summary>
    /// 読めない値は標準に落とす。設定ファイルは手で書き換えられるし、
    /// 版が変われば壊れた値も入りうる。
    /// </summary>
    private static double Normalize(double scale)
        => Steps.Any(s => Math.Abs(s.Value - scale) < 0.001) ? scale : 1.0;

    private static void Save(double scale)
    {
        try
        {
            Settings.Current.UiScale = scale;
            Settings.Save();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "UiScale.Save");
        }
    }
}
