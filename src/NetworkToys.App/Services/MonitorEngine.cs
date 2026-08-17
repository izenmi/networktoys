using System.Threading.Channels;
using NetworkToys.Core.Models;

namespace NetworkToys.App.Services;

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
    private Task? _stopTask;

    public ChannelReader<ProbeResult> Results => _channel.Reader;

    public bool IsRunning { get; private set; }

    public int ActiveCount => _monitors.Count;

    public void Start(IReadOnlyList<Target> targets, MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(settings);

        if (IsRunning)
            throw new InvalidOperationException("すでに測定中です。");

        // BeginStop() で止めた後は後片付けが済んでいない。残っていたら先に畳む
        // （BeginStop はキャンセル済みなので、ここでの Dispose は待たされない）
        foreach (TargetMonitor stale in _monitors)
            stale.Dispose();
        _monitors.Clear();
        _stopTask = null;

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

    public Task StopAsync()
    {
        // BeginStop() の後でも後片付けが要るので、IsRunning ではなく
        // モニタが残っているかで判定する。同時に 2 回呼ばれても
        // （停止ボタン直後のクリア操作など）同じ後片付けを共有する。
        if (_monitors.Count == 0 && _stopTask is null)
            return Task.CompletedTask;

        return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        IsRunning = false;

        // まず全員へ同時に停止を伝えてから、まとめて待つ
        foreach (TargetMonitor monitor in _monitors)
            monitor.BeginStop();

        List<TargetMonitor> monitors = [.. _monitors];
        _monitors.Clear();

        List<Task> stops = [.. monitors.Select(m => m.StopAsync())];
        try
        {
            // 後片付けは待つが、長くは待たない。進行中のプローブが
            // タイムアウトするまで付き合う必要はない。
            await Task.WhenAll(stops).WaitAsync(StopTimeout);
        }
        catch (Exception)
        {
            // 待ちきれなかった分も、1 本が例外で終わった分も、下で選り分ける。
            // ここで抜けると呼び出し元の「停止しました」表示まで届かない
        }

        // ループが終わったものだけ片付ける。生きているループの Ping を先に
        // Dispose すると、進行中の SendPingAsync が ObjectDisposedException を
        // 投げて crash.log を汚す。残りはプロセス終了と GC に任せる
        bool allStopped = true;
        for (int i = 0; i < monitors.Count; i++)
        {
            if (stops[i].IsCompleted)
                monitors[i].Dispose();
            else
                allStopped = false;
        }

        // 止まりきっていないモニタは _concurrency をまだ待っているかもしれない
        if (allStopped)
            _concurrency?.Dispose();

        _concurrency = null;
        _settings = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}
