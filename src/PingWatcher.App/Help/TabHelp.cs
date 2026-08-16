namespace PingWatcher.App.Help;

/// <summary>
/// タブごとの ⓘ に出す説明。
///
/// <b>XAML の属性ではなくここに置く。</b>属性値だと改行が入れられず
/// （<c>&amp;#10;</c> を並べることになる）、書くのもレビューするのも成り立たない。
/// 画面からは <c>{x:Static help:TabHelp.Ping}</c> で引く。
/// 試験のひな型（<c>Core/Verify/RecommendedChecks</c>）と同じ流儀。
///
/// <b>書き方の決まり</b>（自己診断が守る）:
/// <list type="bullet">
///   <item>1 行目はタブ名。浮いた箱だけ見えている状態で、何の説明か分かるように</item>
///   <item>節（この画面ですること／知っておくとよいこと／気をつけること／できる操作）と
///         <c>・</c> の箇条書きで書く。地の文を続けない</item>
///   <item><b>1 行は全角 34 字まで。</b>折り返しを当てにしない
///         （折り返すと箇条書きの頭が揃わず、かえって読みにくい）。
///         収まらないなら 2 行に割り、続きは全角スペースで下げる</item>
/// </list>
/// </summary>
internal static class TabHelp
{
    /// <summary>Ping</summary>
    public const string Ping =
        """
        応答しない宛先が他を待たせることはありません。2 回続けて応答が無い宛先は上段の「応答なし」欄に浮き上がるので、落ちた瞬間を見逃しません。行を選ぶと下に平均・ジッタなどの詳しい統計が出ます。行を右クリックすると、その相手を TCP Ping に足したり、SSH / Telnet でつないだり、経路や名前を調べたりできます。状態は記号と文字で出ます（● 応答 / ✕ 不通 / ⊘ 拒否 / ? 名前 / … 停止）。
        """;

    /// <summary>Wi-Fi</summary>
    public const string Wifi =
        """
        接続中 AP の詳細、受信強度（RSSI）の推移、周辺 AP のチャンネル混雑グラフ。「保存」でいまの状況をテキストに残せます。Windows 11 24H2 以降は OS の仕様で位置情報の許可が要ります。許可が無いと OS が情報を返さないので、画面の案内から設定を開いてください。許可を求める時機が読めなくなるため、この画面を開くまでスキャンはしません。
        """;

    /// <summary>差分比較</summary>
    public const string Diff =
        """
        対象は show run（既定）/ show ip route / そのまま比較。複数台ぶんを同時に抱えられるので、作業前にまとめて貼っておき、作業後にもう半分を貼るのが定石です。show ip route は行の差分ではなく構造として突き合わせるため、経過時間だけが違う行は差分になりません。「前の差分」「次の差分」で長い設定でもたどれます。
        """;

    /// <summary>WFP</summary>
    public const string Wfp =
        """
        WFP は Windows Filtering Platform の略で、ファイアウォールやセキュリティ製品が通信を落とすときに通る仕組みです。いつ・どのアプリが・どこへ出ようとして落とされたかが分かります。読み取りにも管理者権限が要ります。さらに Windows は既定でこの記録を取っていないので、0 件のままなら「記録を有効にする」を押してください（PC 全体に効く設定で、閉じるとき元に戻します）。ここに出るのはこの PC の WFP が落としたものだけで、相手側やネットワーク機器で落とされた通信は出ません。プロセスが「—」なのは不具合ではありません。受信の遮断は通信が届く前に落ちているので、持ち主のアプリが存在しません。
        """;

    /// <summary>IP設定</summary>
    public const string IpConfig =
        """
        アダプタを選ぶと現在の設定が入力欄に入ります。DHCP と固定 IP を切り替えられ、よく使う現場の設定はプリセットとして名前を付けて保存できます。適用のときだけ UAC の確認が 1 回出ます（アプリ自体は管理者になりません）。リモートデスクトップ越しの操作では、適用した瞬間に通信が切れることがあるので注意してください。下段のプロキシ（使わない / PAC / 固定サーバ）は管理者権限も UAC も要りません。
        """;

    /// <summary>業務確認</summary>
    public const string Verify =
        """
        ネットワークやプロキシを入れ替えたあとの業務確認試験を、判定できるところまで肩代わりします。プロキシを切り替えて同じ試験を回せるので、入れ替えの前後を並べて比べられます。HTTP は応答コードだけでは合格にしません（遮断ページは HTTP 200 で返るため、本文と最終 URL も見ます）。Teams は音声が通る UDP まで確かめます。ブラウザでしか開けないページは開くところまで肩代わりし、合否は人が付けます。結果は CSV と HTML の報告書にできます。
        """;

    /// <summary>調べる</summary>
    public const string Probe =
        """
        TCP Ping・Traceroute・スキャン・DNS・SNMP Get・通信状況・サブネット計算。どれも単発で使う道具なので、1 つにまとめて畳んであります。
        """;

    /// <summary>TCP Ping</summary>
    public const string Tcp =
        """
        結果は 3 状態で出ます — つながる / 拒否（ホストは生きているがポートが閉じている）/ 無応答。この区別が切り分けの決め手になります。宛先は Ping とは別に持ち、末尾の :ポート がつなぐ先です（省略すると画面の既定ポート）。測定用の接続はデータを流さず即座に閉じるので、相手に負担はかけません。
        """;

    /// <summary>Traceroute</summary>
    public const string Trace =
        """
        TTL を並列に投げるので速く終わります（レート制限を避けるため少しずつずらして投げます）。経路の見張りは ECMP による誤検知を避けるため、2 回続けて変わったときだけ「変化」と判定します。Path MTU にはブラックホールがあり、ICMP を返さないルータがあると「大きすぎる」ではなく「無応答」になります。両者は書き分けています。
        """;

    /// <summary>スキャン</summary>
    public const string Scan =
        """
        応答した機器の MAC・ベンダー名・ホスト名を一覧にします。そのまま宛先リストへ追加できます。ポート調査は既定で無効です。有効にすると、よく使う 22 ポートへ順に接続を試みます。ウイルス対策ソフトや EDR が反応することがあるので、自分が管理権限を持つネットワークでのみ使ってください。
        """;

    /// <summary>DNS</summary>
    public const string Dns =
        """
        「社内 DNS では引けるが外部では引けない」の切り分けに使います。A / AAAA / CNAME / MX / NS / TXT / SOA / SRV / PTR に対応。
        """;

    /// <summary>通信状況</summary>
    public const string Connections =
        """
        TCP / UDP の接続をプロセスごとにまとめて表示します（リソースモニター風）。既定は一時停止で、開いた瞬間の 1 枚だけを出します。外すと 2 秒ごとに追いかけます。通信量（送受信 B/秒）だけは管理者権限が要ります（ETW という OS の仕組みの制限）。非管理者では「—」になり、案内から昇格して起動し直せます。接続一覧そのものは非管理者で見えます。
        """;

    /// <summary>サブネット計算</summary>
    public const string Subnet =
        """
        ネットワークアドレス / ブロードキャスト / ホスト範囲 / 分割案を表示します。/8〜/32 のマスク早見表つき。現場で暗算しないための道具です。
        """;

    /// <summary>NW機器</summary>
    public const string Devices =
        """
        ログ採取（Cisco 機器へ入って show を集める）・showコマンド整形（出力を CSV に）・Meraki（ダッシュボード API からの照会）。
        """;

    /// <summary>ログ採取</summary>
    public const string Collect =
        """
        作業前後の証跡取りを、機器 1 台ずつ手で打つ代わりに一括で済ませられます。「宛先リストから選ぶ」で Ping / TCP に登録した相手を取り込めます（備考も一緒に入ります）。覚えるのはユーザー名だけです。パスワードと enable パスワードは保存しません。危ないコマンド（reload・configure terminal・write など）は実行前に弾きます。機器が確認を求めてくるため自動では答えられず、投げても止まるだけだからです。
        """;

    /// <summary>showコマンド整形</summary>
    public const string Convert =
        """
        対応は show ip route / ip interface brief / cdp neighbors / mac address-table / interfaces status / inventory / version / logging の 8 種。種類は自動で判定します。BOM 付き UTF-8 で保存するので、日本語版 Excel でそのまま開けます。
        """;

    /// <summary>Meraki</summary>
    public const string Meraki =
        """
        ネットワーク / 機器（シリアル・ファーム）/ MX のアップリンクとグローバル IP / クライアント。各一覧は CSV で保存できます。API キーは保存しません（毎回入力・伏せ字）。画面の PNG 保存にも、管理者として再起動するときの引き継ぎにも残しません。このタブを開いただけでは通信しません。キーを入れて押したときだけ取りに行きます。
        """;

    /// <summary>ネットワーク</summary>
    public const string MerakiNetworks =
        """
        組織の中のネットワーク（拠点）を一覧にします。ここで選んだネットワークが、機器・アップリンク・クライアントの照会先になります。まずここから選んでください。20 ページで打ち切り、打ち切ったときはその旨を画面に出します（黙って切ると「その拠点は無い」と誤読されるため）。
        """;

    /// <summary>機器</summary>
    public const string MerakiDevices =
        """
        そのネットワークにある機器の一覧です。型番・シリアル・ファームウェアの版・稼働状態が分かるので、版ずれの洗い出しや棚卸しに使えます。行を右クリックすると、その機器を Ping やログ採取へ打ち直しなしで渡せます。
        """;

    /// <summary>アップリンク</summary>
    public const string MerakiUplinks =
        """
        MX のアップリンク（WAN 側）の状態と、外から見えるグローバル IP です。回線が生きているか、いまどちらの回線を使っているか、切り替わっていないかが分かります。拠点から外へ出られないという申告の切り分けは、まずここを見ます。
        """;

    /// <summary>クライアント</summary>
    public const string MerakiClients =
        """
        そのネットワークに繋がっている端末の一覧です。VLAN と最終確認時刻も出るので、「その端末はいまどこに居るか」「本当に繋がっているか」を確かめられます。VLAN は機器によって数値と文字列が混ざるため、そのまま文字として出しています。行を右クリックすると Ping や経路調査へ渡せます。
        """;

    /// <summary>SNMP Get</summary>
    public const string SnmpGet =
        """
        sysDescr などよく使う OID のプリセットがあります。Trap の受信は「受ける」の中にあります。
        """;

    /// <summary>受ける</summary>
    public const string Receive =
        """
        FTP / TFTP / SFTP / syslog / SNMP Trap。どれも使い捨てで、押したときだけ待ち受けます。FTP と TFTP は平文・認証なしなので、使うときだけ開始してください。初回は Windows ファイアウォールの許可が要ります。
        """;

    /// <summary>FTP</summary>
    public const string Ftp =
        """
        受け取ったファイルは ftp\ フォルダに保存されます。画面にそのまま打てるコマンド例が出ます。平文・認証ありでもパスワードは流れるので、使うときだけ開始してください。PASV / PORT 両対応。
        """;

    /// <summary>TFTP</summary>
    public const string Tftp =
        """
        受け取ったファイルは tftp\ フォルダへ。blksize の交渉に対応しています。認証がまったく無いので、使うときだけ開始してください。
        """;

    /// <summary>SFTP</summary>
    public const string Sftp =
        """
        WinSCP などから sftp\ フォルダへ読み書きできます。ホスト鍵は初回に生成して保存します。平文で困る場面ではこちらを使ってください。
        """;

    /// <summary>syslog</summary>
    public const string Syslog =
        """
        機器側に logging <この PC の IP> を入れるだけです。受信内容は logs\ にも残ります。重大度の列があり、err 以下は赤・warning は橙で出ます（色だけでなく文字でも出ます）。「warning 以上」「err 以上」に絞れます。絞っても待受は止まりません。初回は Windows ファイアウォールの許可が要ります。
        """;

    /// <summary>SNMP Trap</summary>
    public const string SnmpTrap =
        """
        varbind は件数で打ち切りません（肝心の中身が 7 個目以降に来る機器があるため）。長い行は一覧で省略し、全文はマウスを載せると出ます。初回は Windows ファイアウォールの許可が要ります。
        """;
}
