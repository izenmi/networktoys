using System.Globalization;
using System.IO;
using System.Windows;
using PingWatcher.App.Mvvm;

namespace PingWatcher.App.ViewModels;

/// <summary>
/// 一覧の列幅をまとめて持つ。見出しの境目をドラッグして変えられる。
///
/// 行は仮想化された DataTemplate で、見出しとは別の Grid になる。だから幅は
/// ここ 1 か所に持って両方から参照する（キーは "テーブル名.列番号"）。
/// 終了時に settings.json へ保存し、次回も同じ幅で開く。
///
/// <b>ドラッグの符号は「可変幅(星)列より左か右か」で決まる。</b>
/// 星列より左の列は右端の境界を掴むのでそのまま足し、星列より右の列は
/// 左端の境界を掴むので引く。これを間違えると境界がカーソルから離れて
/// ドラッグ量が暴走する（Ping 一覧で実際に起きた）。判断を各所に散らさず、
/// <see cref="Tables"/> の宣言 1 か所から機械的に決める。
/// </summary>
public sealed class TableColumns : ObservableObject
{
    /// <summary>
    /// 1 つの表の宣言。
    /// </summary>
    /// <param name="Name">キーの前半。</param>
    /// <param name="FirstStar">最初の可変幅(星)列の番号。これより右の列は符号が逆になる。</param>
    /// <param name="Columns">幅を持つ列の (列番号, 既定幅)。星列は含めない。</param>
    private sealed record TableSpec(string Name, int FirstStar, (int Column, double Width)[] Columns);

    // 列を足し引きしたらここも直すこと。番号は XAML の Grid.Column と 1 対 1
    private static readonly TableSpec[] Tables =
    [
        // 経路: 0 ホップ / 1 応答元 / 2 ホスト名(星) / 3 RTT / 4 損失 / 5 備考
        new("trace", 2, [(0, 34), (1, 140), (3, 140), (4, 70), (5, 120)]),

        // 無線: 0 SSID(星) / 1 BSSID / 2 信号 / 3 ch / 4 規格 / 5 機器 / 6 暗号
        new("wifi", 0, [(1, 140), (2, 132), (3, 150), (4, 76), (5, 56), (6, 72)]),

        // スキャン: 0 IP / 1 RTT / 2 ホスト名(星) / 3 MAC / 4 ベンダー / 5 ポート / 6 備考
        new("scan", 2, [(0, 116), (1, 62), (3, 140), (4, 132), (5, 160), (6, 150)]),

        // ファイル配布の記録(FTP/TFTP/SFTP): 0 時刻 / 1 相手 / 2 内容(星)
        new("ftplog", 2, [(0, 72), (1, 130)]),
        new("tftplog", 2, [(0, 72), (1, 130)]),
        new("sftplog", 2, [(0, 72), (1, 130)]),

        // syslog: 0 時刻 / 1 送信元 / 2 本文(星)
        new("syslog", 2, [(0, 72), (1, 130)]),

        // SNMP GET: 0 OID / 1 名前 / 2 型 / 3 値(星)
        new("snmpget", 3, [(0, 220), (1, 130), (2, 90)]),

        // SNMP Trap: 0 時刻 / 1 送信元 / 2 内容(星)
        new("snmptrap", 2, [(0, 72), (1, 130)]),

        // 接続: 0 プロトコル / 1 ローカル(星) / 2 リモート(星) / 3 状態 / 4 送信 / 5 受信
        new("conn", 1, [(0, 56), (3, 110), (4, 76), (5, 76)]),

        // 遮断: 0 時刻 / 1 方向 / 2 プロトコル / 3 送信元(星) / 4 宛先(星) / 5 プロセス / 6 件数 / 7 フィルタ
        new("wfp", 3, [(0, 96), (1, 66), (2, 66), (5, 130), (6, 52), (7, 96)]),

        // Meraki ネットワーク: 0 名前(星) / 1 ID / 2 製品 / 3 タイムゾーン / 4 タグ
        new("mnet", 0, [(1, 150), (2, 150), (3, 110), (4, 120)]),

        // Meraki 機器: 0 名前(星) / 1 型番 / 2 シリアル / 3 ファーム / 4 NW / 5 状態 / 6 GIP / 7 LAN
        new("mdev", 0, [(1, 70), (2, 130), (3, 110), (4, 120), (5, 76), (6, 120), (7, 120)]),

        // Meraki アップリンク: 0 NW(星) / 1 シリアル / 2 回線 / 3 状態 / 4 IP / 5 GW / 6 GIP
        new("mup", 0, [(1, 130), (2, 60), (3, 80), (4, 120), (5, 120), (6, 120)]),

        // Meraki クライアント: 0 名前(星) / 1 IP / 2 MAC / 3 VLAN / 4 メーカー / 5 通信量 / 6 最終確認
        new("mcli", 0, [(1, 120), (2, 130), (3, 55), (4, 110), (5, 90), (6, 150)]),
    ];

    private readonly Dictionary<string, double> _widths = [];

    public static TableColumns Instance { get; } = Load();

    /// <summary>XAML から <c>Path=[conn.0]</c> の形で引く。</summary>
    public GridLength this[string key]
        => _widths.TryGetValue(key, out double width) ? new GridLength(width) : new GridLength(100);

    /// <summary>境目を掴んで動かす。表ごとの星列の位置から符号を決める。</summary>
    public void Drag(string key, double horizontalChange)
    {
        if (!_widths.TryGetValue(key, out double current)) return;

        int dot = key.LastIndexOf('.');
        if (dot <= 0 || !int.TryParse(key[(dot + 1)..], CultureInfo.InvariantCulture, out int column))
            return;

        TableSpec? spec = Array.Find(Tables, t => t.Name == key[..dot]);
        if (spec is null) return;

        // 星列より右の列は左端の境界を掴んでいるので逆向きに動く
        double delta = column < spec.FirstStar ? horizontalChange : -horizontalChange;

        _widths[key] = Math.Clamp(current + delta, 36, 900);

        // インデクサ全体の変更を知らせる。どのキーが変わったかは WPF に見分けられない
        OnPropertyChanged("Item[]");
    }

    /// <summary>アプリを閉じるときに呼ぶ。書けなくても落とさない。</summary>
    public void Save()
    {
        try
        {
            Settings.Current.TableColumns = new Dictionary<string, double>(_widths);
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 列幅は失っても困らない
        }
    }

    private static TableColumns Load()
    {
        var layout = new TableColumns();

        foreach (TableSpec spec in Tables)
        {
            foreach ((int column, double width) in spec.Columns)
                layout._widths[$"{spec.Name}.{column}"] = width;
        }

        foreach ((string key, double width) in Settings.Current.TableColumns)
        {
            // 桁が壊れた設定で一覧が潰れないよう、読める範囲だけ受け入れる。
            // 知らないキー(列を減らした後の残骸)は捨てる
            if (layout._widths.ContainsKey(key) && width is >= 36 and <= 900)
                layout._widths[key] = width;
        }

        return layout;
    }
}
