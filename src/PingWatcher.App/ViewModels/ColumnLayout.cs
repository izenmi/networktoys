using System.Globalization;
using System.IO;
using System.Windows;
using PingWatcher.App.Mvvm;

namespace PingWatcher.App.ViewModels;

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

    /// <summary>既定幅。文字の大きさに合わせて伸ばすので、素の値はここ 1 か所に持つ。</summary>
    private static readonly (double State, double Target, double Rtt, double Loss, double Spark)
        Defaults = (84, 128, 60, 70, 92);

    /// <summary>
    /// 既定に戻す。ドラッグで崩したときの逃げ道
    /// （これが無いと settings.json を直接編集するしかない）。
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

        Save();
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

        Save();
    }

    /// <summary>ドラッグと同じ範囲に収める。</summary>
    private static GridLength Fit(double width) => new(Math.Clamp(Math.Round(width), 36, 600));

    /// <summary>アプリを閉じるときに呼ぶ。書けなくても落とさない。</summary>
    public void Save()
    {
        try
        {
            Settings.Current.Columns =
                [State.Value, Target.Value, Rtt.Value, Loss.Value, Spark.Value];
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 列幅は失っても困らない
        }
    }

    private static ColumnLayout Load()
    {
        var layout = new ColumnLayout();

        List<double> widths = Settings.Current.Columns;
        if (widths.Count != 5)
            return layout;

        GridLength Read(double value, GridLength fallback)
            => value is >= 36 and <= 600 ? new GridLength(value) : fallback;

        layout._state = Read(widths[0], layout._state);
        layout._target = Read(widths[1], layout._target);
        layout._rtt = Read(widths[2], layout._rtt);
        layout._loss = Read(widths[3], layout._loss);
        layout._spark = Read(widths[4], layout._spark);

        return layout;
    }
}
