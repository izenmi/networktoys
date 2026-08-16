using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;

namespace PingWatcher.App.ViewModels;

/// <summary>重大度での絞り込みの選択肢。<paramref name="Max"/> 以下だけ残す（-1 は絞らない）。</summary>
public sealed record SeverityFilter(string Label, int Max);

/// <summary>
/// syslog サーバ画面。機器からの syslog を受けて一覧に出す。
/// 共通部分は <see cref="FileServerViewModel"/>（開始/停止・履歴・ログ）。
/// </summary>
public sealed class SyslogViewModel : FileServerViewModel
{
    public SyslogViewModel(string? localAddress) : base("syslog", 514, localAddress)
    {
    }

    /// <summary>
    /// シビリティは <b>0=emerg 〜 7=debug で小さいほど重い</b>ので、
    /// 「err 以上」は「3 以下」を意味する。
    /// </summary>
    public SeverityFilter[] SeverityFilters { get; } =
    [
        new("すべて", -1),
        new("warning 以上", 4),
        new("err 以上", 3),
    ];

    private SeverityFilter _selectedSeverity = null!;

    public SeverityFilter SelectedSeverity
    {
        get => _selectedSeverity ??= SeverityFilters[0];
        set
        {
            if (value is null || !SetProperty(ref _selectedSeverity, value)) return;

            MinSeverity = value.Max;
        }
    }

    // 受け取るだけでフォルダは使わないが、基底がログ用に要求するので logs 直下を返す
    public override string RootDirectory => SessionLogService.LogsDirectory;

    public override string CommandHint
        => $"機器側で「logging {HostForHint}」を設定すると、ここに届きます。\n" +
           "（Cisco IOS の例。機器によって書き方は異なります）";

    private protected override IFileServer CreateServer(int port) => new SyslogReceiver();
}
