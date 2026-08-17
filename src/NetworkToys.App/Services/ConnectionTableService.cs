using System.ComponentModel;
using System.Diagnostics;
using NetworkToys.Core.Net;

namespace NetworkToys.App.Services;

/// <summary>
/// 接続表の読み取りとプロセス名の解決をまとめた 1 回ぶんのスナップショット取得。
/// ThreadPool 上で呼ぶこと（プロセス名の解決がぶら下がる）。
///
/// 名前は PID → 名前のキャッシュで持ち、毎ティックの全プロセス列挙はしない
/// （接続表に現れる PID は数十個で、ティック間の新顔は通常ゼロ〜数個）。
/// </summary>
internal sealed class ConnectionTableService
{
    private readonly Dictionary<int, string> _names = [];

    public ConnectionSnapshot Read()
    {
        List<ConnectionRow> rows = Interop.NativeMethods.GetConnectionTable();

        var pids = new HashSet<int>();
        foreach (ConnectionRow row in rows)
            pids.Add(row.Pid);

        foreach (int pid in pids)
        {
            // PID 0(所有プロセスなし)と 4(System)は名前が引けない。Core の既定名に任せる
            if (pid is 0 or 4 || _names.ContainsKey(pid))
                continue;

            try
            {
                using Process process = Process.GetProcessById(pid);
                _names[pid] = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // 終了済み・アクセス不可。辞書に入れなければ Core 側が「PID n」と表示する
            }
        }

        // 接続表から消えた PID は捨てる。PID が再利用されたときの名前のすり替わり対策も兼ねる
        List<int> stale = _names.Keys.Where(pid => !pids.Contains(pid)).ToList();
        foreach (int pid in stale)
            _names.Remove(pid);

        return new ConnectionSnapshot(rows, new Dictionary<int, string>(_names));
    }
}

/// <summary>接続表 1 回ぶんの読み取り結果。</summary>
internal sealed record ConnectionSnapshot(
    IReadOnlyList<ConnectionRow> Rows,
    IReadOnlyDictionary<int, string> ProcessNames)
{
    public static readonly ConnectionSnapshot Empty = new([], new Dictionary<int, string>());
}
