using System.Threading.Channels;
using PastelNet.Core.Models;

namespace PastelNet.App.Services;

/// <summary>
/// 宛先ごとの <see cref="TargetMonitor"/> をまとめて起動・停止する。
///
/// 結果は Channel に流すだけで、UI へは触らない。UI 側が自分の都合の良い頻度で
/// まとめて取り出す（1 件ごとに Dispatcher を叩くと数百宛先で描画が破綻するため）。
/// </summary>
internal sealed class MonitorEngine : IAsyncDisposable
{
    private readonly Channel<ProbeResult> _channel = Channel.CreateUnbounded<ProbeResult>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly List<TargetMonitor> _monitors = [];
    private SemaphoreSlim? _concurrency;

    public ChannelReader<ProbeResult> Results => _channel.Reader;

    public bool IsRunning { get; private set; }

    public int ActiveCount => _monitors.Count;

    public void Start(IReadOnlyList<Target> targets, MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(settings);

        if (IsRunning)
            throw new InvalidOperationException("すでに測定中です。");

        _concurrency = new SemaphoreSlim(settings.MaxConcurrency, settings.MaxConcurrency);

        foreach (Target target in targets)
        {
            if (!target.Enabled || !target.IsValid())
                continue;

            // Phase 1 では ICMP のみ。TCP は Phase 2 で追加する。
            if (target.Kind != ProbeKind.Icmp)
                continue;

            var monitor = new TargetMonitor(target, settings, _channel.Writer, _concurrency);
            _monitors.Add(monitor);
            monitor.Start();
        }

        IsRunning = true;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        // 全宛先へ同時に停止を伝える。1 件ずつ待つと宛先数 × タイムアウトだけかかる。
        await Task.WhenAll(_monitors.Select(m => m.StopAsync()));

        foreach (TargetMonitor monitor in _monitors)
            monitor.Dispose();

        _monitors.Clear();
        _concurrency?.Dispose();
        _concurrency = null;
        IsRunning = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}
