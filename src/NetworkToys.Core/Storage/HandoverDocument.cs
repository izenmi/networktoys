using System.Text.Json;

namespace NetworkToys.Core.Storage;

/// <summary>
/// 管理者権限で起動し直すときに、いまの状態をそのまま次のプロセスへ渡すための入れ物。
///
/// <b>settings.json とは役割が違う。</b>あちらは「次に起動したときも残っていてほしい設定」で、
/// こちらは「いま画面に出ているもの」— 測定の途中経過や貼り付けたテキスト。
/// 一度きりの受け渡しなので、<b>読んだ側が真っ先に消す</b>（機器の出力には
/// enable secret のような認証情報が入りうるので、ディスクに置きっぱなしにしない）。
/// </summary>
public sealed class HandoverDocument
{
    public int Version { get; set; } = 1;

    /// <summary>測定が動いていたか。動いていたなら引き継いだ側で再開する。</summary>
    public bool WasRunning { get; set; }

    /// <summary>TCP 側の測定が動いていたか。</summary>
    public bool TcpWasRunning { get; set; }

    /// <summary>選択していたタブの見出し。</summary>
    public string SelectedTab { get; set; } = "";

    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Ping 側の測定状態。</summary>
    public List<HandoverTarget> Targets { get; set; } = [];

    /// <summary>TCP 側の測定状態。</summary>
    public List<HandoverTarget> TcpTargets { get; set; } = [];

    public HandoverPanels Panels { get; set; } = new();
}

/// <summary>
/// 宛先 1 つ分の測定状態。
///
/// 履歴は 1 サンプル 1 オブジェクトにせず<b>3 本の配列</b>で持つ。
/// 300 サンプル × 数百宛先になるので、キー名を毎回書くと桁が変わる。
/// </summary>
public sealed class HandoverTarget
{
    /// <summary>Host|Kind|Port。Target.Id は読み直すたびに変わるので鍵にしない。</summary>
    public string Key { get; set; } = "";

    /// <summary>名前解決の結果。引き継いだ直後に「—」に戻らないように。</summary>
    public string Address { get; set; } = "";

    public List<long> Ticks { get; set; } = [];
    public List<double> Rtt { get; set; } = [];
    public List<int> Status { get; set; } = [];

    public HandoverWindow Window { get; set; } = new();
}

/// <summary>作業開始以降の集計。履歴から数え直せないのでそのまま控える。</summary>
public sealed class HandoverWindow
{
    public long StartedAtTicks { get; set; }
    public int Attempts { get; set; }
    public int Successes { get; set; }
    public int Responses { get; set; }
    public double SumMs { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public int MaxConsecutiveFailures { get; set; }
    public int ConsecutiveFailures { get; set; }
}

/// <summary>各画面の入力欄。打ち直しが面倒なものだけを拾う。</summary>
public sealed class HandoverPanels
{
    /// <summary>差分比較。機器ごとに貼り付けた出力を丸ごと持つ。</summary>
    public List<HandoverDevice> Devices { get; set; } = [];

    public string SelectedDevice { get; set; } = "";
    public int SelectedMode { get; set; }
    public string DiffNote { get; set; } = "";

    public string ConvertInput { get; set; } = "";
    public string DnsHost { get; set; } = "";
    public string ScanRange { get; set; } = "";
    public string SnmpHost { get; set; } = "";
    public string SnmpCommunity { get; set; } = "";
    public string SnmpOid { get; set; } = "";
    public string TraceHost { get; set; } = "";
    public string SubnetInput { get; set; } = "";
    public string ConnectionFilter { get; set; } = "";
    public string WfpFilter { get; set; } = "";

    // Meraki の API キーは<b>意図して入れない</b>。保存しない決まりで伏せ字にしているのに、
    // 引き継ぎファイルへ書けば平文でディスクに落ちる。昇格後に入れ直してもらう。
}

/// <summary>差分比較の機器 1 台分。</summary>
public sealed class HandoverDevice
{
    public string Name { get; set; } = "";
    public List<HandoverPaste> Pasted { get; set; } = [];
}

/// <summary>貼り付けた出力 1 種類分（作業前・作業後の対）。</summary>
public sealed class HandoverPaste
{
    public int Kind { get; set; }
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
}

/// <summary>引き継ぎファイルの読み書き。</summary>
public static class HandoverStore
{
    public static void Save(string path, HandoverDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // 引き継ぎは一度きりなので、設定ファイルのような tmp→rename の手当ては要らない。
        // 途中で落ちたら読み手が捨てるだけ（版番号が合わなければ無視される）
        File.WriteAllText(path, JsonSerializer.Serialize(document, NetworkToysJsonContext.Default.HandoverDocument));
    }

    /// <summary>
    /// 読んで<b>必ず消す</b>。中身に機器の認証情報が入りうるので、
    /// 読めても読めなくてもファイルは残さない。
    /// </summary>
    public static HandoverDocument? LoadAndDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            string json = File.ReadAllText(path);
            HandoverDocument? document =
                JsonSerializer.Deserialize(json, NetworkToysJsonContext.Default.HandoverDocument);

            // 版が違うものを当てにいかない。引き継げないより、壊れて起動しない方が困る
            return document is { Version: 1 } ? document : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>UAC を断られたときなど、引き継がずに終わる場合の後始末。</summary>
    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても続ける。%TEMP% なのでいずれ片付く
        }
    }
}
