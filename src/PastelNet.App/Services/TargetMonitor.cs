using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using PastelNet.Core.Models;

namespace PastelNet.App.Services;

/// <summary>
/// 宛先 1 件を独立した周期で測り続ける。
///
/// このツールの核心は「宛先ごとに独立して回す」こと。全宛先を 1 ラウンドずつ
/// まとめて待つ方式だと、応答しない 1 宛先がタイムアウトするまで他の全宛先が
/// 待たされる。EXPing の遅さと同じ轍を踏まないため、宛先ごとにループを持たせる。
/// </summary>
internal sealed class TargetMonitor : IDisposable
{
    private readonly Target _target;
    private readonly MonitorSettings _settings;
    private readonly ChannelWriter<ProbeResult> _writer;
    private readonly SemaphoreSlim _concurrency;

    // Ping は同時に 1 リクエストしか扱えない（進行中に再度呼ぶと InvalidOperationException）。
    // 1 宛先 = 1 インスタンスで、その中では逐次に使う。
    private readonly Ping _ping = new();
    private readonly byte[] _payload;
    private readonly PingOptions _pingOptions = new() { DontFragment = false };

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IPAddress? _address;

    public TargetMonitor(Target target, MonitorSettings settings, ChannelWriter<ProbeResult> writer, SemaphoreSlim concurrency)
    {
        _target = target;
        _settings = settings;
        _writer = writer;
        _concurrency = concurrency;
        _payload = new byte[Math.Clamp(settings.PayloadBytes, 0, 65_500)];
        Array.Fill(_payload, (byte)'p');
    }

    public string TargetId => _target.Id;

    public void Start()
    {
        if (_loop is not null) return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_loop is not null)
                await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止要求による打ち切りは正常
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        int intervalMs = _target.IntervalMs ?? _settings.IntervalMs;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));

        // 最初の 1 回はティックを待たずに測る。起動直後に画面が埋まる方が気持ちよい。
        do
        {
            try
            {
                ProbeResult result = await ProbeOnceAsync(token);
                await _writer.WriteAsync(result, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 1 宛先の失敗でループごと死なせない
                CrashLog.Write(ex, $"TargetMonitor({_target.Host})");
            }
        }
        while (await timer.WaitForNextTickAsync(token));
    }

    private async Task<ProbeResult> ProbeOnceAsync(CancellationToken token)
    {
        long now = DateTime.Now.Ticks;

        IPAddress? address = _address ?? await ResolveAsync(token);
        if (address is null)
            return new ProbeResult(_target.Id, ProbeSample.Failure(now, ProbeStatus.DnsFailure), null);

        int timeoutMs = _target.TimeoutMs ?? _settings.TimeoutMs;

        // 同時に飛ばすパケット数の上限。数百宛先でも一斉には出さない。
        await _concurrency.WaitAsync(token);
        try
        {
            long startedAt = Stopwatch.GetTimestamp();
            PingReply reply = await _ping.SendPingAsync(
                address,
                TimeSpan.FromMilliseconds(timeoutMs),
                _payload,
                _pingOptions,
                token);

            // PingReply.RoundtripTime は Status が Success 以外だと 0 が返るうえ
            // 分解能も粗いので、自前で測った値を使う。
            double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            ProbeSample sample = reply.Status switch
            {
                IPStatus.Success => ProbeSample.Success(now, elapsedMs),
                IPStatus.TimedOut => ProbeSample.Failure(now, ProbeStatus.TimedOut),
                IPStatus.DestinationHostUnreachable
                    or IPStatus.DestinationNetworkUnreachable
                    or IPStatus.DestinationUnreachable
                    or IPStatus.DestinationPortUnreachable
                    or IPStatus.DestinationProhibited => ProbeSample.Failure(now, ProbeStatus.Unreachable),
                _ => ProbeSample.Failure(now, ProbeStatus.Error),
            };

            return new ProbeResult(_target.Id, sample, address.ToString());
        }
        catch (PingException)
        {
            return new ProbeResult(_target.Id, ProbeSample.Failure(now, ProbeStatus.Error), address.ToString());
        }
        catch (SocketException)
        {
            return new ProbeResult(_target.Id, ProbeSample.Failure(now, ProbeStatus.Error), address.ToString());
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// ホスト名を IP に解決してキャッシュする。毎回 DNS を引くと宛先が増えたときに重くなる。
    /// 解決できなかった場合はキャッシュせず、次の周期でもう一度試す。
    /// </summary>
    private async Task<IPAddress?> ResolveAsync(CancellationToken token)
    {
        if (IPAddress.TryParse(_target.Host, out IPAddress? parsed))
        {
            _address = parsed;
            return parsed;
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(_target.Host, token);
            _address = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?? (addresses.Length > 0 ? addresses[0] : null);
            return _address;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _ping.Dispose();
    }
}
