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

    // 備考を宛先の右隣に置いてから(ユーザー指示)、可変幅の星列は末尾の推移。
    // 保存形式は列構成が変わったので v2(旧形式は既定幅で開き直す)
    private const string FormatMarker = "v2";

    private GridLength _state = new(66);
    private GridLength _target = new(140);
    private GridLength _note = new(110);
    private GridLength _rtt = new(62);
    private GridLength _loss = new(48);

    public GridLength State { get => _state; set => SetProperty(ref _state, value); }
    public GridLength Target { get => _target; set => SetProperty(ref _target, value); }
    public GridLength Note { get => _note; set => SetProperty(ref _note, value); }
    public GridLength Rtt { get => _rtt; set => SetProperty(ref _rtt, value); }
    public GridLength Loss { get => _loss; set => SetProperty(ref _loss, value); }

    /// <summary>アプリを閉じるときに呼ぶ。書けなくても落とさない。</summary>
    public void Save()
    {
        try
        {
            string line = FormatMarker + '\t' + string.Join('\t',
                new[] { State, Target, Note, Rtt, Loss }
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
            if (parts.Length != 6 || parts[0] != FormatMarker) return layout;

            GridLength Read(string text, GridLength fallback)
                => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                   && value is >= 36 and <= 600
                    ? new GridLength(value)
                    : fallback;

            layout._state = Read(parts[1], layout._state);
            layout._target = Read(parts[2], layout._target);
            layout._note = Read(parts[3], layout._note);
            layout._rtt = Read(parts[4], layout._rtt);
            layout._loss = Read(parts[5], layout._loss);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めなければ既定の幅で開く
        }

        return layout;
    }
}
