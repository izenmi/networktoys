using System.Globalization;
using System.IO;
using System.Text;

namespace PingWatcher.App.Services;

/// <summary>
/// 測定ログの書き込み係。測定開始ごとに logs フォルダへ 1 ファイル作り、
/// 整形済みの行を追記していく。
///
/// 方針:
/// ・UI スレッドではバッファに積むだけ。ファイル I/O は約 2 秒ごとに ThreadPool で行う
///   （共有フォルダ上で動かされたとき、書き込みの待ちで測定の取り込みを止めない）。
/// ・追記のみ。途中でクラッシュしても、それまでの行はファイルに残っている。
/// ・書けなくても測定は止めない。失敗は初回だけ crash.log に残して黙る。
/// </summary>
internal sealed class SessionLogService : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    /// <summary>BOM 付き UTF-8。メモ帳と Excel で文字化けさせない（レポートと同じ判断）。</summary>
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly object _gate = new();      // バッファとパスを守る（UI スレッドが触る）
    private readonly object _ioGate = new();    // 書き込みの順序を守る（ThreadPool が触る）
    private readonly StringBuilder _buffer = new();

    private string? _path;
    private Timer? _timer;
    private bool _failureLogged;

    public static string LogsDirectory => AppData.PathOf("logs");

    /// <summary>スクリーンショットの保存先。ログと同じフォルダに置く。</summary>
    public static string NewScreenshotPath()
        => Path.Combine(LogsDirectory, $"screen-{Stamp()}.png");

    /// <summary>新しいログファイルを開き、ヘッダ行を積む。</summary>
    public void Start(string prefix, IEnumerable<string> headerLines)
    {
        lock (_gate)
        {
            _path = Path.Combine(LogsDirectory, $"{prefix}-{Stamp()}.log");

            foreach (string line in headerLines)
                _buffer.Append(line).Append("\r\n");
        }

        _timer ??= new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    /// <summary>1 行積む。ファイルにはまだ書かない。</summary>
    public void Append(string line)
    {
        lock (_gate)
        {
            if (_path is null) return;
            _buffer.Append(line).Append("\r\n");
        }
    }

    /// <summary>末尾行を積み、書き切ってからファイルを閉じたことにする。</summary>
    public void Stop(string footerLine)
    {
        lock (_gate)
        {
            if (_path is null) return;
            _buffer.Append(footerLine).Append("\r\n");
        }

        Flush();

        lock (_gate)
            _path = null;
    }

    /// <summary>溜まっている行をファイルへ追記する。</summary>
    public void Flush()
    {
        // I/O の錠を先に取ることで、書き込みの順序がバッファから取り出した順と一致する。
        // UI スレッドの Append は _gate しか取らないので、書き込み中も待たされない
        lock (_ioGate)
        {
            string? path;
            string chunk;

            lock (_gate)
            {
                if (_path is null || _buffer.Length == 0) return;
                path = _path;
                chunk = _buffer.ToString();
                _buffer.Clear();
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, chunk, Utf8Bom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    CrashLog.Write(ex, "SessionLogService.Flush");
                }
            }
        }
    }

    /// <summary>
    /// 30 日より古いログを片付ける。起動時に ThreadPool で一度呼ぶ。
    /// スクリーンショット(.png)は消さない — 明示的に撮った証跡なので勝手に処分しない。
    /// </summary>
    public static void CleanupOldLogs()
    {
        try
        {
            string directory = LogsDirectory;
            if (!Directory.Exists(directory)) return;

            DateTime threshold = DateTime.Now.AddDays(-30);

            foreach (string file in Directory.EnumerateFiles(directory, "*.log"))
            {
                if (File.GetLastWriteTime(file) < threshold)
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 片付けられなくても実害はない。次の起動でまた試す
        }
    }

    public void Dispose()
    {
        Flush();
        _timer?.Dispose();
        _timer = null;
    }

    private static string Stamp()
        => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}
