using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using PingWatcher.App.Mvvm;
using PingWatcher.Core.Addressing;

namespace PingWatcher.App.ViewModels;

/// <summary>分割案の 1 段分（見出しと子サブネットの行）。</summary>
public sealed record SubnetSplitGroup(string Header, IReadOnlyList<string> Rows);

/// <summary>早見表の 1 行。</summary>
public sealed record MaskCheatSheetRow(string Prefix, string Mask, string Hosts);

/// <summary>
/// サブネット電卓。計算は <see cref="SubnetCalculator"/>（Core・テスト済み）に任せ、
/// ここでは表示用の文字列に整えるだけ。
/// </summary>
public sealed class SubnetViewModel : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _debounce;

    private string _input;
    private string _errorText = string.Empty;
    private bool _hasResult;
    private string _networkText = string.Empty;
    private string _broadcastText = string.Empty;
    private string _maskText = string.Empty;
    private string _rangeText = string.Empty;
    private string _hostCountText = string.Empty;

    /// <param name="initialCidr">既定の入力。自分のいるサブネットを入れておくとすぐ試せる。</param>
    public SubnetViewModel(string? initialCidr = null)
    {
        _input = string.IsNullOrWhiteSpace(initialCidr) ? "192.168.1.0/24" : initialCidr;

        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = Debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Recalculate();
        };

        Recalculate();
    }

    /// <summary>
    /// 早見表。/8〜/32 のマスクとホスト数。アプリ全体で 1 つあれば足りる。
    /// 手書きの表は写し間違えるので、計算部と同じ IpMath から起こす。
    /// </summary>
    public static IReadOnlyList<MaskCheatSheetRow> CheatSheet { get; } = BuildCheatSheet();

    public ObservableCollection<SubnetSplitGroup> Splits { get; } = [];

    public string Input
    {
        get => _input;
        set
        {
            if (!SetProperty(ref _input, value)) return;

            // 打ち終わってから計算する（1 文字ごとにエラーを出すとうるさい）
            _debounce.Stop();
            _debounce.Start();
        }
    }

    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public bool HasResult { get => _hasResult; private set => SetProperty(ref _hasResult, value); }
    public string NetworkText { get => _networkText; private set => SetProperty(ref _networkText, value); }
    public string BroadcastText { get => _broadcastText; private set => SetProperty(ref _broadcastText, value); }
    public string MaskText { get => _maskText; private set => SetProperty(ref _maskText, value); }
    public string RangeText { get => _rangeText; private set => SetProperty(ref _rangeText, value); }
    public string HostCountText { get => _hostCountText; private set => SetProperty(ref _hostCountText, value); }

    private void Recalculate()
    {
        SubnetInfo? info = SubnetCalculator.Parse(Input, out string? error);

        ErrorText = error ?? string.Empty;
        HasResult = info is not null;
        Splits.Clear();

        if (info is null)
        {
            NetworkText = BroadcastText = MaskText = RangeText = HostCountText = string.Empty;
            return;
        }

        NetworkText = info.Network.ToString();
        BroadcastText = info.Broadcast.ToString();
        MaskText = string.Create(CultureInfo.InvariantCulture, $"/{info.PrefixLength}（{info.Mask}）");
        RangeText = $"{info.FirstHost} 〜 {info.LastHost}";
        HostCountText = info.UsableHosts.ToString("N0", CultureInfo.InvariantCulture) + " 台";

        // 1〜3 段深い分割案。/30 より先は範囲が自明なので出しすぎない
        for (int newPrefix = info.PrefixLength + 1; newPrefix <= Math.Min(info.PrefixLength + 3, 32); newPrefix++)
        {
            IReadOnlyList<SubnetInfo> children = SubnetCalculator.Split(info, newPrefix);
            int count = SubnetCalculator.SplitCount(info.PrefixLength, newPrefix);

            var rows = new List<string>(children.Count + 1);
            rows.AddRange(children.Select(c =>
                $"{c.Cidr}　{c.FirstHost} 〜 {c.LastHost}"));

            if (count > children.Count)
                rows.Add(string.Create(CultureInfo.InvariantCulture, $"…ほか {count - children.Count:N0} 個"));

            Splits.Add(new SubnetSplitGroup(
                string.Create(CultureInfo.InvariantCulture, $"/{newPrefix} に分割すると {count:N0} 個"),
                rows));
        }
    }

    /// <summary>入力を既定へ戻す。</summary>
    public void Reset()
    {
        Input = "192.168.1.0/24";
        _debounce.Stop();
        Recalculate();
    }

    private static IReadOnlyList<MaskCheatSheetRow> BuildCheatSheet()
    {
        var rows = new List<MaskCheatSheetRow>(25);

        for (int prefix = 8; prefix <= 32; prefix++)
        {
            rows.Add(new MaskCheatSheetRow(
                $"/{prefix}",
                Core.Addressing.IpMath.FromUInt32(Core.Addressing.IpMath.PrefixToMask(prefix)).ToString(),
                Core.Addressing.IpMath.UsableHostCount(prefix).ToString("N0", CultureInfo.InvariantCulture)));
        }

        return rows;
    }
}
