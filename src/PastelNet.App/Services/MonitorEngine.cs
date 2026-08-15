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
    /// <summary>停止の後片付けを待つ上限。</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly Channel<ProbeResult> _channel = Channel.CreateUnbounded<ProbeResult>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly List<TargetMonitor> _monitors = [];
    private SemaphoreSlim? _concurrency;
    private MonitorSettings? _settings;

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
        _settings = settings;
        IsRunning = true;

        foreach (Target target in targets)
            AddTarget(target);
    }

    /// <summary>
    /// 測定を止めずに宛先を 1 件足す。
    /// 宛先ごとに独立したループなので、他の宛先には何の影響もない。
    /// </summary>
    public void AddTarget(Target target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!IsRunning || _settings is null || _concurrency is null)
            return;

        if (!target.Enabled || !target.IsValid())
            return;

        var monitor = new TargetMonitor(target, _settings, _channel.Writer, _concurrency);
        _monitors.Add(monitor);
        monitor.Start();
    }

    /// <summary>測定を止めずに宛先を 1 件外す。</summary>
    public async Task RemoveTargetAsync(string targetId)
    {
        TargetMonitor? monitor = _monitors.FirstOrDefault(m => string.Equals(m.TargetId, targetId, StringComparison.Ordinal));
        if (monitor is null) return;

        _monitors.Remove(monitor);

        await monitor.StopAsync();
        monitor.Dispose();
    }

    /// <summary>
    /// キャンセルを投げるだけで完了は待たない。アプリを閉じるときに使う。
    /// 待つと、名前解決中の宛先があるだけで数秒固まる。
    /// </summary>
    public void BeginStop()
    {
        foreach (TargetMonitor monitor in _monitors)
            monitor.BeginStop();

        IsRunning = false;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        // まず全員へ同時に停止を伝えてから、まとめて待つ
        foreach (TargetMonitor monitor in _monitors)
            monitor.BeginStop();

        try
        {
            // 後片付けは待つが、長くは待たない。進行中のプローブが
            // タイムアウトするまで付き合う必要はない。
            await Task.WhenAll(_monitors.Select(m => m.StopAsync())).WaitAsync(StopTimeout);
        }
        catch (TimeoutException)
        {
            // 残りは Dispose とプロセス終了に任せる
        }

        foreach (TargetMonitor monitor in _monitors)
            monitor.Dispose();

        _monitors.Clear();
        _concurrency?.Dispose();
        _concurrency = null;
        _settings = null;
        IsRunning = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}
