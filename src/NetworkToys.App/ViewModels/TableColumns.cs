using System.Globalization;
using System.IO;
using System.Windows;
using NetworkToys.App.Mvvm;

namespace NetworkToys.App.ViewModels;

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
        // 経路: 0 ホップ / 1 応答元 / 2 ホスト名(星) / 3 RTT / 4 状態
        new("trace", 2, [(0, 34), (1, 140), (3, 140), (4, 70)]),

        // 無線: 0 SSID(星) / 1 BSSID / 2 信号 / 3 ch / 4 規格 / 5 機器 / 6 暗号
        new("wifi", 0, [(1, 140), (2, 132), (3, 150), (4, 76), (5, 56), (6, 72)]),

        // スキャン: 0 IP / 1 RTT / 2 ホスト名(星) / 3 MAC / 4 ベンダー / 5 ポート / 6 備考
        new("scan", 2, [(0, 116), (1, 62), (3, 140), (4, 132), (5, 160), (6, 150)]),

        // ファイル配布の記録(FTP/TFTP/SFTP): 0 時刻 / 1 相手 / 2 内容(星)
        new("ftplog", 2, [(0, 72), (1, 130)]),
        new("tftplog", 2, [(0, 72), (1, 130)]),
        new("sftplog", 2, [(0, 72), (1, 130)]),

        // ファイル転送の 2 ペイン: 0 種類 / 1 名前(星) / 2 サイズ / 3 更新日時
        // 左右で同じ形の表なので、宣言も列番号も揃えてある
        new("local", 1, [(0, 26), (2, 84), (3, 116)]),
        new("remote", 1, [(0, 26), (2, 84), (3, 116)]),

        // syslog: 0 時刻 / 1 送信元 / 2 重大度 / 3 本文(星)
        new("syslog", 3, [(0, 72), (1, 130), (2, 64)]),

        // SNMP GET: 0 OID / 1 名前 / 2 型 / 3 値(星)
        new("snmpget", 3, [(0, 220), (1, 130), (2, 90)]),

        // SNMP Trap: 0 時刻 / 1 送信元 / 2 内容(星)
        new("snmptrap", 2, [(0, 72), (1, 130)]),

        // 接続: 0 プロトコル / 1 ローカル / 2 リモート(星) / 3 状態 / 4 送信 / 5 受信
        // 星列が 2 つ並んでいるとその境目だけ動かせなくなるので、星は 1 表に 1 つに絞る
        new("conn", 2, [(0, 56), (1, 190), (3, 110), (4, 76), (5, 76)]),

        // 遮断: 0 時刻 / 1 方向 / 2 プロトコル / 3 送信元 / 4 宛先(星) / 5 プロセス / 6 件数
        // (フィルタ ID と層の列は「役に立たない」として 2026-08-16 に削除。CSV には残す)
        new("wfp", 4, [(0, 124), (1, 66), (2, 66), (3, 170), (5, 130), (6, 52)]),

        // 収集: 0 削除 / 1 アドレス(星) / 2 接続方法 / 3 ユーザー名 / 4 パスワード / 5 enable /
        //       6 備考 / 7 状態
        // 削除は 16px の ✕ だけ。見出しの「削除」が入る幅までしか詰めない。
        // 固定幅の列が 7 つあるので、既定の窓幅ではアドレス(星)が数文字ぶんまで
        // 潰れて IP が読めなかった(2026-08-18 報告)。ほかを詰めたうえで、
        // XAML 側の星列に MinWidth を入れて潰れきらないようにしてある
        new("collect", 1, [(0, 28), (2, 72), (3, 80), (4, 80), (5, 80), (6, 110), (7, 110)]),

        // 確認(試験項目): 0 削除 / 1 項目名(星) / 2 種類 / 3 宛先 / 4 期待 / 5 結果
        // 削除の列には ✕ と ▶ の 2 つが並ぶ(2+16+3+16=37px)。ほかの表の削除列(28px)を
        // 写すと ▶ が切れる(2026-08-18 報告)。
        // 星(余りを吸う列)は宛先ではなく項目名(2026-08-18 ユーザー指示で入れ替えた)
        new("verify", 1, [(0, 42), (2, 76), (3, 176), (4, 84), (5, 170)]),

        // 確認(結果): 0 項目 / 1 プロキシ / 2 合否 / 3 所要 / 4 詳細(星)
        new("vres", 4, [(0, 150), (1, 150), (2, 130), (3, 62)]),

        // Meraki 機器: 0 名前(星) / 1 型番 / 2 シリアル / 3 ファーム / 4 NW / 5 状態 / 6 LAN
        new("mdev", 0, [(1, 70), (2, 130), (3, 150), (4, 120), (5, 76), (6, 130)]),

        // Meraki アップリンク: 0 NW(星) / 1 シリアル / 2 回線 / 3 状態 / 4 IP / 5 GW / 6 GIP
        new("mup", 0, [(1, 130), (2, 60), (3, 80), (4, 120), (5, 120), (6, 120)]),

        // Meraki クライアント: 0 拠点 / 1 名前(星) / 2 IP / 3 MAC / 4 VLAN / 5 メーカー / 6 通信量 / 7 最終確認
        new("mcli", 1, [(0, 130), (2, 120), (3, 130), (4, 55), (5, 110), (6, 90), (7, 150)]),

        // Meraki 拠点: 0 拠点(星) / 1 台数 / 2 セグメント / 3 備考
        new("mrsite", 0, [(1, 110), (2, 260), (3, 200)]),

        // Meraki DHCP: 0 拠点 / 1 機器 / 2 VLAN / 3 サブネット(星) / 4 払い出し / 5 空き / 6 使用率
        new("mrdhcp", 3, [(0, 130), (1, 130), (2, 60), (4, 100), (5, 90), (6, 80)]),

        // Meraki アラート: 0 重大度 / 1 種別 / 2 拠点 / 3 機器 / 4 発生 / 5 内容(星)
        new("mralert", 5, [(0, 80), (1, 120), (2, 120), (3, 120), (4, 150)]),


        // Meraki 導入時確認: 0 項目 / 1 対象 / 2 判定 / 3 詳細(星)
        // 判定は試験タブの合否列と同じ幅。○✕ のボタンが右端に載る
        new("mrcheck", 3, [(0, 150), (1, 170), (2, 130)]),

        // ACI ヘルス: 0 種別 / 1 名前(星) / 2 スコア / 3 状態
        new("acihl", 1, [(0, 90), (2, 60), (3, 80)]),

        // ACI フォールト: 0 重大度 / 1 コード / 2 発生 / 3 対象(星) / 4 説明 / 5 確認
        new("aciflt", 3, [(0, 80), (1, 70), (2, 140), (4, 300), (5, 50)]),

        // ACI ポート: 0 ノード / 1 IF / 2 状態 / 3 速度 / 4 VLAN / 5 タグ / 6 PC /
        //             7 EPG(星) / 8 理由 / 9 最終変化 / 10 説明
        new("aciport", 7, [(0, 60), (1, 90), (2, 80), (3, 70), (4, 130), (5, 80), (6, 80),
                           (8, 110), (9, 130), (10, 180)]),

        // ACI BD: 0 テナント / 1 BD / 2 VRF / 3 ルーティング / 4 サブネット(星) / 5 知らない宛先
        new("acibd", 4, [(0, 110), (1, 160), (2, 120), (3, 90), (5, 140)]),

        // ACI EPG: 0 テナント / 1 アプリ / 2 EPG(星) / 3 BD / 4 ドメイン / 5 静的パス
        new("aciepg", 2, [(0, 110), (1, 110), (3, 130), (4, 160), (5, 70)]),

        // ACI EPG メンバー: 0 ノード / 1 パス(星) / 2 VLAN / 3 モード
        new("aciepgm", 1, [(0, 80), (2, 110), (3, 90)]),

        // ACI エンドポイント: 0 MAC / 1 IP / 2 テナント / 3 EPG(星) / 4 VLAN / 5 ノード / 6 パス
        new("aciep", 3, [(0, 140), (1, 130), (2, 110), (4, 90), (5, 70), (6, 160)]),

        // ACI ログ: 0 時刻 / 1 種別 / 2 重大度 / 3 対象 / 4 内容(星)
        new("acilog", 4, [(0, 140), (1, 110), (2, 80), (3, 200)]),

        // ACI 機器一覧: 0 ノード / 1 名前(星) / 2 役割 / 3 型番 / 4 シリアル / 5 版 / 6 状態
        new("acidev", 1, [(0, 70), (2, 80), (3, 130), (4, 140), (5, 150), (6, 90)]),







        // Catalyst Center 端末: 0 MAC / 1 IP / 2 名前 / 3 接続 / 4 機器(星) / 5 ポート・AP /
        //                       6 VLAN / 7 SSID / 8 帯域 / 9 健全度 / 10 サイト / 11 更新
        //
        // 状態を出す列（健全度・到達性・結果）は記号と言葉を並べるので、見た目より広く要る。
        // 足りないと右端が黙って切れる（2026-08-17 に実機で報告された）
        new("dnccl", 4, [(0, 125), (1, 105), (2, 100), (3, 60), (5, 130), (6, 55),
                         (7, 90), (8, 65), (9, 120), (10, 130), (11, 120)]),

        // Catalyst Center 端末の一覧: 0 MAC / 1 IP / 2 名前 / 3 接続 / 4 機器 / 5 ポート・AP /
        //                             6 SSID / 7 帯域 / 8 健全度 / 9 サイト(星)
        new("dnccll", 9, [(0, 125), (1, 105), (2, 110), (3, 60), (4, 140), (5, 130),
                          (6, 100), (7, 65), (8, 120)]),

        // Catalyst Center イベント: 0 時刻 / 1 種別 / 2 結果 / 3 発生元 / 4 詳細(星)
        new("dncev", 4, [(0, 140), (1, 150), (2, 90), (3, 130)]),

        // Catalyst Center 機器: 0 機器 / 1 型番 / 2 シリアル / 3 版 / 4 IP / 5 サイト(星) / 6 役割 /
        //                       7 到達性 / 8 健全度
        new("dncdev", 5, [(0, 150), (1, 130), (2, 120), (3, 85), (4, 110), (6, 85), (7, 100), (8, 120)]),

        // Catalyst Center 保守と適合: 0 機器 / 1 種別 / 2 状態 / 3 日付 / 4 備考(星)
        new("dnclc", 4, [(0, 130), (1, 180), (2, 140), (3, 140)]),
    ];

    private readonly Dictionary<string, double> _widths = [];

    public static TableColumns Instance { get; } = Load();

    /// <summary>XAML から <c>Path=[conn.0]</c> の形で引く。</summary>
    public GridLength this[string key]
        => _widths.TryGetValue(key, out double width) ? new GridLength(width) : new GridLength(100);

    /// <summary>いまの幅。</summary>
    public double WidthOf(string key) => _widths.TryGetValue(key, out double width) ? width : 100;

    /// <summary>掴んでいる境目（左右の列と、掴んだ時点の幅）。</summary>
    private (string Key, string? Left, string? Right, double LeftWidth, double RightWidth) _grip;

    /// <summary>
    /// つまみが表しているのは「列」ではなく<b>列と列の境目</b>。
    ///
    /// 星列より左の列は右端に、右の列は左端につまみを置いてあるので、
    /// つまみの列番号から境目の左右が決まる。星列は幅を持たない（null）。
    /// </summary>
    private (string? Left, string? Right) BoundaryOf(string key)
    {
        int dot = key.LastIndexOf('.');

        if (dot <= 0 || !int.TryParse(key[(dot + 1)..], CultureInfo.InvariantCulture, out int column))
            return (null, null);

        string table = key[..dot];
        TableSpec? spec = Array.Find(Tables, t => t.Name == table);

        if (spec is null) return (null, null);

        int left = column < spec.FirstStar ? column : column - 1;

        return (Sized(spec, left), Sized(spec, left + 1));
    }

    /// <summary>その列が幅を持つなら鍵を返す。星列は持たないので null。</summary>
    private static string? Sized(TableSpec spec, int column)
        => Array.Exists(spec.Columns, c => c.Column == column) ? $"{spec.Name}.{column}" : null;

    /// <summary>つまみを掴んだ。境目の左右の幅を控える。</summary>
    public void BeginResize(string key)
    {
        (string? left, string? right) = BoundaryOf(key);

        _grip = (key, left, right, left is null ? 0 : WidthOf(left), right is null ? 0 : WidthOf(right));
    }

    /// <summary>
    /// つまみを掴んでからの<b>総移動量</b>で、境目の左右の列だけを伸縮させる。
    ///
    /// <b>境目の左右だけが動き、ほかの列は幅も位置も変わらない</b>（2026-08-18 ユーザー指示）。
    /// 以前は掴んだ列だけを伸縮させ、余りを星列に吸わせていたので、離れた列が
    /// 勝手に伸び縮みして見えた。片側が星列のときは、もう片側を動かせば
    /// 星列が同じだけ吸うので結果は同じになる。
    ///
    /// <b>1 回ぶんの差分（<c>DragDelta.HorizontalChange</c>）を足し込んではいけない。</b>
    /// 幅を変えるとつまみ自体が動くので、WPF の <c>Thumb</c> は自分の移動ぶんを差し引いた
    /// 値を返す。差分を足していくと伸び縮みが鈍り、端で詰まると飛ぶ
    /// （2026-08-17 にユーザーが実機で「変な動き」として報告）。
    /// </summary>
    public void Resize(string key, double totalChange)
    {
        if (_grip.Key != key) BeginResize(key);

        (_, string? left, string? right, double leftWidth, double rightWidth) = _grip;

        if (left is null && right is null) return;

        // どちらかが下限・上限で詰まったら、そこで移動量を止める。
        // 止めないと境目がカーソルから離れて、片側だけが伸び続ける
        double delta = totalChange;

        if (left is not null) delta = Fit(leftWidth + delta) - leftWidth;
        if (right is not null) delta = rightWidth - Fit(rightWidth - delta);

        if (left is not null) _widths[left] = Fit(leftWidth + delta);
        if (right is not null) _widths[right] = Fit(rightWidth - delta);

        // インデクサ全体の変更を知らせる。どのキーが変わったかは WPF に見分けられない
        OnPropertyChanged("Item[]");
    }

    /// <summary>掴まずに動かす（自己診断など）。</summary>
    public void Drag(string key, double horizontalChange)
    {
        BeginResize(key);
        Resize(key, horizontalChange);
    }

    /// <summary>
    /// 既定に戻す。ドラッグで崩したときの逃げ道
    /// （幅は保存していないので、開き直しても戻せる。押せばその場で戻る）。
    /// 既定値は <see cref="Tables"/> の宣言が持っているので、そこから引き直す。
    /// <b>いまの文字の大きさに合わせた幅</b>に戻す — 素の値のままだと、
    /// 文字を大きくしている人にとっては狭すぎる。
    /// </summary>
    public void Reset()
    {
        double scale = UiScale.Current;

        foreach (TableSpec spec in Tables)
        {
            foreach ((int column, double width) in spec.Columns)
                _widths[$"{spec.Name}.{column}"] = Fit(width * scale);
        }

        OnPropertyChanged("Item[]");
    }

    /// <summary>
    /// 文字の大きさが変わったぶんだけ、いまの幅を伸ばす（縮める）。
    /// <b>個別に広げた分の割合は保たれる</b>ので、調整をやり直さずに済む。
    /// </summary>
    public void Scale(double ratio)
    {
        if (ratio <= 0 || Math.Abs(ratio - 1) < 0.001) return;

        foreach (string key in _widths.Keys.ToArray())
            _widths[key] = Fit(_widths[key] * ratio);

        OnPropertyChanged("Item[]");
    }

    /// <summary>ドラッグと同じ範囲に収める。</summary>
    private static double Fit(double width) => Math.Clamp(Math.Round(width), 36, 900);

    /// <summary>
    /// 起動のたびに宣言の値から組み直す。<b>列幅は保存しない</b>
    /// （2026-08-18 ユーザー指示。毎回そろった幅で始める）。
    /// </summary>
    private static TableColumns Load()
    {
        var layout = new TableColumns();

        // 宣言の値は「標準の文字の大きさ」のもの。文字を大きくしている人には
        // そのままでは狭いので、読んだ倍率を掛けてから置く（Reset と同じ考え）
        double scale = UiScale.Current;

        foreach (TableSpec spec in Tables)
        {
            foreach ((int column, double width) in spec.Columns)
                layout._widths[$"{spec.Name}.{column}"] = width * scale;
        }

        return layout;
    }
}
