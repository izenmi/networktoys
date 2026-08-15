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
    private const string FileName = "columns.txt";

    public static ColumnLayout Instance { get; } = Load();

    private GridLength _state = new(66);
    private GridLength _target = new(140);
    private GridLength _rtt = new(62);
    private GridLength _loss = new(48);
    private GridLength _spark = new(80);

    public GridLength State { get => _state; set => SetProperty(ref _state, value); }
    public GridLength Target { get => _target; set => SetProperty(ref _target, value); }
    public GridLength Rtt { get => _rtt; set => SetProperty(ref _rtt, value); }
    public GridLength Loss { get => _loss; set => SetProperty(ref _loss, value); }
    public GridLength Spark { get => _spark; set => SetProperty(ref _spark, value); }

    /// <summary>アプリを閉じるときに呼ぶ。書けなくても落とさない。</summary>
    public void Save()
    {
        try
        {
            string line = string.Join('\t',
                new[] { State, Target, Rtt, Loss, Spark }
                    .Select(w => w.Value.ToString("0", CultureInfo.InvariantCulture)));

            File.WriteAllText(AppData.PathOf(FileName), line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 列幅は失っても困らない
        }
    }

    private static ColumnLayout Load()
    {
        var layout = new ColumnLayout();

        try
        {
            string path = AppData.PathOf(FileName);
            if (!File.Exists(path)) return layout;

            string[] parts = File.ReadAllText(path).Trim().Split('\t');
            if (parts.Length != 5) return layout;

            GridLength Read(string text, GridLength fallback)
                => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                   && value is >= 36 and <= 600
                    ? new GridLength(value)
                    : fallback;

            layout._state = Read(parts[0], layout._state);
            layout._target = Read(parts[1], layout._target);
            layout._rtt = Read(parts[2], layout._rtt);
            layout._loss = Read(parts[3], layout._loss);
            layout._spark = Read(parts[4], layout._spark);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めなければ既定の幅で開く
        }

        return layout;
    }
}
