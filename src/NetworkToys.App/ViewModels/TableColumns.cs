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

        // 収集: 0 削除 / 1 アドレス(星) / 2 接続方法 / 3 ユーザー名 / 4 パスワード / 5 enable / 6 状態
        // 削除は 16px の ✕ だけ。見出しの「削除」が入る幅までしか詰めない
        new("collect", 1, [(0, 28), (2, 80), (3, 90), (4, 90), (5, 90), (6, 140), (7, 120)]),

        // 確認(試験項目): 0 削除 / 1 項目名 / 2 種類 / 3 宛先(星) / 4 期待 / 5 結果
        new("verify", 3, [(0, 28), (1, 150), (2, 76), (4, 110), (5, 170)]),

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

        // Meraki 利用率: 0 拠点 / 1 機器(星) / 2 型番 / 3 シリアル / 4 利用率 / 5 備考
        new("mrutil", 1, [(0, 150), (2, 90), (3, 130), (4, 150), (5, 180)]),

        // Meraki 導入時確認: 0 項目 / 1 対象 / 2 判定 / 3 詳細(星)
        // 判定は試験タブの合否列と同じ幅。○✕ のボタンが右端に載る
        new("mrcheck", 3, [(0, 150), (1, 170), (2, 130)]),

        // ACI ヘルス: 0 種別 / 1 名前(星) / 2 スコア / 3 状態
        new("acihl", 1, [(0, 90), (2, 60), (3, 80)]),

        // ACI フォールト: 0 重大度 / 1 コード / 2 発生 / 3 対象(星) / 4 説明 / 5 確認
        new("aciflt", 3, [(0, 80), (1, 70), (2, 140), (4, 300), (5, 50)]),

        // ACI ポート: 0 ノード / 1 IF / 2 状態 / 3 速度 / 4 VLAN / 5 タグ / 6 PC /
        //             7 EPG(星) / 8 理由 / 9 最終変化
        new("aciport", 7, [(0, 60), (1, 90), (2, 80), (3, 70), (4, 110), (5, 80), (6, 80),
                           (8, 110), (9, 130)]),

        // ACI EPG: 0 テナント / 1 アプリ / 2 EPG(星) / 3 BD / 4 ドメイン / 5 静的パス
        new("aciepg", 2, [(0, 110), (1, 110), (3, 130), (4, 160), (5, 70)]),

        // ACI EPG メンバー: 0 ノード / 1 パス(星) / 2 VLAN / 3 モード
        new("aciepgm", 1, [(0, 80), (2, 110), (3, 90)]),

        // ACI エンドポイント: 0 MAC / 1 IP / 2 テナント / 3 EPG(星) / 4 VLAN / 5 ノード / 6 パス
        new("aciep", 3, [(0, 140), (1, 130), (2, 110), (4, 90), (5, 70), (6, 160)]),

        // ACI ログ: 0 時刻 / 1 種別 / 2 重大度 / 3 対象 / 4 内容(星)
        new("acilog", 4, [(0, 140), (1, 110), (2, 80), (3, 200)]),

        // ACI 構成: 0 種別 / 1 名前(星) / 2 親 / 3 状態 / 4 備考
        new("acicfg", 1, [(0, 130), (2, 150), (3, 90), (4, 260)]),

        // WLC 端末: 0 MAC / 1 IP / 2 メーカー / 3 AP(星) / 4 SSID / 5 電波 / 6 RSSI / 7 品質 / 8 速度 / 9 状態
        new("wlccl", 3, [(0, 130), (1, 120), (2, 110), (4, 110), (5, 110), (6, 55), (7, 70), (8, 60), (9, 80)]),

        // WLC AP: 0 状態 / 1 AP(星) / 2 IP / 3 MAC / 4 型番 / 5 版 / 6 無線 / 7 台数 / 8 タグ
        new("wlcap", 1, [(0, 80), (2, 110), (3, 130), (4, 120), (5, 80), (6, 160), (7, 50), (8, 130)]),

        // WLC 参加・切断: 0 状態 / 1 AP(星) / 2 MAC / 3 最終参加 / 4 最終切断 / 5 理由 / 6 参加 / 7 失敗
        new("wlcjoin", 1, [(0, 80), (2, 130), (3, 150), (4, 150), (5, 180), (6, 50), (7, 50)]),

        // WLC SSID: 0 SSID(星) / 1 プロファイル / 2 ID / 3 状態 / 4 台数 / 5 2.4G / 6 5G / 7 6G
        new("wlcssid", 0, [(1, 150), (2, 50), (3, 80), (4, 60), (5, 70), (6, 70), (7, 70)]),

        // WLC 電波: 0 AP(星) / 1 無線 / 2 ch / 3 出力 / 4 使用率 / 5 雑音 / 6 台数
        new("wlcrf", 0, [(1, 90), (2, 50), (3, 60), (4, 70), (5, 60), (6, 50)]),

        // WLC 不正 AP: 0 種別 / 1 SSID(星) / 2 BSSID / 3 メーカー / 4 ch / 5 電波 / 6 検知AP / 7 最終受信 / 8 備考
        new("wlcrog", 1, [(0, 60), (2, 130), (3, 110), (4, 50), (5, 55), (6, 130), (7, 130), (8, 90)]),

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

    /// <summary>境目を掴んで動かす。表ごとの星列の位置から符号を決める。</summary>
    /// <summary>いまの幅。ドラッグの開始時に控えておくために使う。</summary>
    public double WidthOf(string key) => _widths.TryGetValue(key, out double width) ? width : 100;

    /// <summary>
    /// つまみを掴んでからの<b>総移動量</b>で幅を決める。
    ///
    /// <b>1 回ぶんの差分（<c>DragDelta.HorizontalChange</c>）を足し込んではいけない。</b>
    /// 幅を変えるとつまみ自体が動くので、WPF の <c>Thumb</c> は自分の移動ぶんを差し引いた
    /// 値を返す。差分を足していくと伸び縮みが鈍り、端で詰まると飛ぶ
    /// （2026-08-17 にユーザーが実機で「変な動き」として報告）。
    /// </summary>
    public void Resize(string key, double startWidth, double totalChange)
    {
        int dot = key.LastIndexOf('.');
        if (dot <= 0 || !int.TryParse(key[(dot + 1)..], CultureInfo.InvariantCulture, out int column))
            return;

        TableSpec? spec = Array.Find(Tables, t => t.Name == key[..dot]);
        if (spec is null) return;

        // 星列より右の列は左端の境界を掴んでいるので逆向きに動く
        double delta = column < spec.FirstStar ? totalChange : -totalChange;

        _widths[key] = Math.Clamp(startWidth + delta, 36, 900);

        // インデクサ全体の変更を知らせる。どのキーが変わったかは WPF に見分けられない
        OnPropertyChanged("Item[]");
    }

    /// <summary>いまの幅からの相対で動かす（自己診断など、掴んでいないところから呼ぶ用）。</summary>
    public void Drag(string key, double horizontalChange)
    {
        if (_widths.TryGetValue(key, out double current)) Resize(key, current, horizontalChange);
    }

    /// <summary>
    /// 既定に戻す。ドラッグで崩したときの逃げ道
    /// （これが無いと settings.json を直接編集するしかない）。
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
        Save();
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
        Save();
    }

    /// <summary>ドラッグと同じ範囲に収める。</summary>
    private static double Fit(double width) => Math.Clamp(Math.Round(width), 36, 900);

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
