using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using PingWatcher.Core.Models;

namespace PingWatcher.App.Services;

/// <summary>
/// 宛先 1 件を独立した周期で測り続ける。
///
/// このツールの核心は「宛先ごとに独立して回す」こと。全宛先を 1 ラウンドずつ
/// まとめて待つ方式だと、応答しない 1 宛先がタイムアウトするまで他の全宛先が
/// 待たされる。EXPing の遅さと同じ轍を踏まないため、宛先ごとにループを持たせる。
/// </summary>
internal sealed class TargetMonitor : IDisposable
{
    /// <summary>名前解決に見切りをつけるまでの時間。</summary>
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(3);

    private readonly Target _target;
    private readonly MonitorSettings _settings;
    private readonly ChannelWriter<ProbeResult> _writer;
    private readonly SemaphoreSlim _concurrency;

    // Ping は同時に 1 リクエストしか扱えない（進行中に再度呼ぶと InvalidOperationException）。
    // 1 宛先 = 1 インスタンスで、その中では逐次に使う。
    private readonly Ping? _ping;
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

        // ProbeOnceAsync は「TCP 以外はすべて ICMP」と分岐するので、生成条件も同じ形に
        // 揃えておく。ここが「Icmp のとき」だと、測り方を将来 1 つ足した瞬間に
        // _ping が null のまま ICMP 分岐へ落ちる。
        if (target.Kind != ProbeKind.Tcp)
            _ping = new Ping();
    }

    public string TargetId => _target.Id;

    public void Start()
    {
        if (_loop is not null) return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// キャンセルを投げるだけで、完了は待たずに戻る。
    /// アプリを閉じるときはこれで足りる（ソケットはプロセス終了で OS が片付ける）。
    /// </summary>
    public void BeginStop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // すでに止まっている
        }
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

        // 同時に飛ばすプローブ数の上限。数百宛先でも一斉には出さない。
        await _concurrency.WaitAsync(token);
        try
        {
            ProbeSample sample = _target.Kind == ProbeKind.Tcp
                ? await ProbeTcpAsync(address, now, timeoutMs, token)
                : await ProbeIcmpAsync(address, now, timeoutMs, token);

            return new ProbeResult(_target.Id, sample, address.ToString());
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task<ProbeSample> ProbeIcmpAsync(IPAddress address, long now, int timeoutMs, CancellationToken token)
    {
        try
        {
            long startedAt = Stopwatch.GetTimestamp();
            PingReply reply = await _ping!.SendPingAsync(
                address,
                TimeSpan.FromMilliseconds(timeoutMs),
                _payload,
                _pingOptions,
                token);

            // PingReply.RoundtripTime は Status が Success 以外だと 0 が返るうえ
            // 分解能も粗いので、自前で測った値を使う。
            double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            return reply.Status switch
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
        }
        catch (PingException)
        {
            return ProbeSample.Failure(now, ProbeStatus.Error);
        }
        catch (SocketException)
        {
            return ProbeSample.Failure(now, ProbeStatus.Error);
        }
        catch (ObjectDisposedException)
        {
            // 停止のタイムアウト後に Ping が先に片付けられた。終了間際にしか起きず、
            // ここで拾わないと catch-all 経由で crash.log に宛先の数だけ積もる
            return ProbeSample.Failure(now, ProbeStatus.Error);
        }
    }

    /// <summary>
    /// 指定ポートへの接続にかかる時間を測る。ICMP が塞がれた環境ではこちらが頼りになる。
    ///
    /// 結果を 3 つに区別するのが肝。とくに「拒否」は<b>ホストが生きている</b>証拠であり、
    /// 無応答とはまったく意味が違う。
    /// </summary>
    private async Task<ProbeSample> ProbeTcpAsync(IPAddress address, long now, int timeoutMs, CancellationToken token)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        // RST で閉じて TIME_WAIT を残さない。FIN で行儀よく閉じると一時ポートが
        // 約 4 分塞がったままになり、200 宛先 × 1 秒間隔なら数分で 4 万件を超えて
        // AddressNotAvailable が多発、全宛先が一斉に「エラー」になる。
        // 周期測定の接続はデータを流さないので、RST で切っても相手は困らない
        socket.LingerState = new LingerOption(true, 0);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeoutMs);

        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, _target.Port), timeoutCts.Token);

            double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            return ProbeSample.Success(now, elapsedMs);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // RST が即返ってきた。ポートは閉じているがホストは応答している
            double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            return new ProbeSample(now, (float)elapsedMs, ProbeStatus.Refused);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return ProbeSample.Failure(now, ProbeStatus.TimedOut);
        }
        catch (SocketException ex)
        {
            return ProbeSample.Failure(now, ex.SocketErrorCode switch
            {
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => ProbeStatus.Unreachable,
                SocketError.HostNotFound => ProbeStatus.DnsFailure,
                _ => ProbeStatus.Error,
            });
        }
        catch (ObjectDisposedException)
        {
            return ProbeSample.Failure(now, ProbeStatus.Error);
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
            // GetHostAddressesAsync はトークンを渡しても内部のブロッキング解決を
            // 中断できない。待つ側で見切りをつけないと、引けないホスト名が 1 つあるだけで
            // 測定周期が乱れ、終了時にも数秒待たされる。
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(_target.Host, token)
                .WaitAsync(DnsTimeout, token);

            _address = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?? (addresses.Length > 0 ? addresses[0] : null);
            return _address;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or TimeoutException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _ping?.Dispose();
    }
}
