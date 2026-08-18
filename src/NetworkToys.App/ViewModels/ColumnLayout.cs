using System.Globalization;
using System.IO;
using System.Windows;
using NetworkToys.App.Mvvm;

namespace NetworkToys.App.ViewModels;

/// <summary>
/// Ping / TCP 一覧の列幅。ヘッダの境目をドラッグして変えられる。
///
/// 一覧の行は仮想化された DataTemplate で、ヘッダとは別の Grid なので、
/// 幅をここで 1 か所に持って両方から参照する（ヘッダは双方向、行は片方向）。
/// アプリ全体で 1 つ。終了時に保存し、次回起動でも同じ幅で開く。
/// </summary>
public sealed class ColumnLayout : ObservableObject
{
    public static ColumnLayout Instance { get; } = Load();

    // 備考は宛先の右隣(ユーザー指示)で、可変幅の星列。余った幅は備考が吸収し、
    // 推移は固定幅で持つ。保存先は settings.json の columns
    // (状態・宛先・RTT・ロス・推移の順で 5 つ)

    // 既定幅はユーザーのスクリーンショット実測に合わせてある
    private GridLength _state = new(84);
    private GridLength _target = new(128);
    private GridLength _rtt = new(60);
    private GridLength _loss = new(70);
    private GridLength _spark = new(92);

    public GridLength State { get => _state; set => SetProperty(ref _state, value); }
    public GridLength Target { get => _target; set => SetProperty(ref _target, value); }
    public GridLength Rtt { get => _rtt; set => SetProperty(ref _rtt, value); }
    public GridLength Loss { get => _loss; set => SetProperty(ref _loss, value); }
    public GridLength Spark { get => _spark; set => SetProperty(ref _spark, value); }

    // 列の並びは 0 状態 / 1 宛先 / 2 備考(星) / 3 RTT / 4 ロス / 5 推移。
    // つまみは「列」ではなく<b>境目</b>を表す（星列より左は右端、右は左端に置いてある）
    private static readonly Dictionary<string, (string? Left, string? Right)> Boundaries = new()
    {
        ["State"] = ("State", "Target"),
        ["Target"] = ("Target", null),      // 右は備考(星)
        ["Rtt"] = (null, "Rtt"),            // 左は備考(星)
        ["Loss"] = ("Rtt", "Loss"),
        ["Spark"] = ("Loss", "Spark"),
    };

    private (string Key, double Left, double Right) _grip;

    private GridLength Get(string? name) => name switch
    {
        "State" => State,
        "Target" => Target,
        "Rtt" => Rtt,
        "Loss" => Loss,
        "Spark" => Spark,
        _ => default,
    };

    private void Set(string? name, double width)
    {
        switch (name)
        {
            case "State": State = Fit(width); break;
            case "Target": Target = Fit(width); break;
            case "Rtt": Rtt = Fit(width); break;
            case "Loss": Loss = Fit(width); break;
            case "Spark": Spark = Fit(width); break;
        }
    }

    /// <summary>つまみを掴んだ。境目の左右の幅を控える。</summary>
    public void BeginResize(string key)
    {
        (string? left, string? right) = Boundaries.TryGetValue(key, out var pair) ? pair : (null, null);

        _grip = (key, Get(left).Value, Get(right).Value);
    }

    /// <summary>
    /// つまみを掴んでからの<b>総移動量</b>で、境目の左右の列だけを伸縮させる。
    ///
    /// <b>ほかの列は幅も位置も変わらない</b>（2026-08-18 ユーザー指示）。表の一覧
    /// （<see cref="TableColumns"/>）と同じ考え方で、片側が星列（備考）のときは
    /// もう片側だけを動かす（星列が同じだけ吸うので結果は同じ）。
    /// </summary>
    public void Resize(string key, double totalChange)
    {
        if (!Boundaries.TryGetValue(key, out (string? Left, string? Right) side)) return;

        if (_grip.Key != key) BeginResize(key);

        double delta = totalChange;

        if (side.Left is not null) delta = Fit(_grip.Left + delta).Value - _grip.Left;
        if (side.Right is not null) delta = _grip.Right - Fit(_grip.Right - delta).Value;

        if (side.Left is not null) Set(side.Left, _grip.Left + delta);
        if (side.Right is not null) Set(side.Right, _grip.Right - delta);
    }

    /// <summary>既定幅。文字の大きさに合わせて伸ばすので、素の値はここ 1 か所に持つ。</summary>
    private static readonly (double State, double Target, double Rtt, double Loss, double Spark)
        Defaults = (84, 128, 60, 70, 92);

    /// <summary>
    /// 既定に戻す。ドラッグで崩したときの逃げ道
    /// （幅は保存していないので、開き直しても戻せる。押せばその場で戻る）。
    /// <b>いまの文字の大きさに合わせた幅</b>に戻す — 素の値のままだと、
    /// 文字を大きくしている人にとっては狭すぎる。
    /// </summary>
    public void Reset()
    {
        double scale = UiScale.Current;

        State = Fit(Defaults.State * scale);
        Target = Fit(Defaults.Target * scale);
        Rtt = Fit(Defaults.Rtt * scale);
        Loss = Fit(Defaults.Loss * scale);
        Spark = Fit(Defaults.Spark * scale);
    }

    /// <summary>
    /// 文字の大きさが変わったぶんだけ、いまの幅を伸ばす（縮める）。
    /// <b>個別に広げた分の割合は保たれる</b>ので、調整をやり直さずに済む。
    /// </summary>
    public void Scale(double ratio)
    {
        if (ratio <= 0 || Math.Abs(ratio - 1) < 0.001) return;

        State = Fit(State.Value * ratio);
        Target = Fit(Target.Value * ratio);
        Rtt = Fit(Rtt.Value * ratio);
        Loss = Fit(Loss.Value * ratio);
        Spark = Fit(Spark.Value * ratio);
    }

    /// <summary>ドラッグと同じ範囲に収める。</summary>
    private static GridLength Fit(double width) => new(Math.Clamp(Math.Round(width), 36, 600));

    /// <summary>
    /// 起動のたびに既定から組み直す。<b>列幅は保存しない</b>
    /// （2026-08-18 ユーザー指示。毎回そろった幅で始める）。
    /// </summary>
    private static ColumnLayout Load() => new();
}
