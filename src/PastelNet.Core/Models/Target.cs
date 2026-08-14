namespace PastelNet.Core.Models;

public enum ProbeKind
{
    /// <summary>ICMP エコー要求。管理者権限は要らない。</summary>
    Icmp = 0,

    /// <summary>指定ポートへの TCP 接続。ICMP が塞がれた環境ではこちらが主役になる。</summary>
    Tcp,
}

/// <summary>監視する宛先 1 件。</summary>
public sealed class Target
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>ホスト名または IP アドレス。</summary>
    public string Host { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    /// <summary>表示上のまとまり。空でもよい。</summary>
    public string Group { get; set; } = string.Empty;

    public ProbeKind Kind { get; set; } = ProbeKind.Icmp;

    /// <summary><see cref="ProbeKind.Tcp"/> のときの接続先ポート。</summary>
    public int Port { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>この宛先だけ測定間隔を変えたいときに設定する。null なら全体設定に従う。</summary>
    public int? IntervalMs { get; set; }

    /// <summary>この宛先だけタイムアウトを変えたいときに設定する。null なら全体設定に従う。</summary>
    public int? TimeoutMs { get; set; }

    public bool IsValid() => !string.IsNullOrWhiteSpace(Host)
                            && (Kind != ProbeKind.Tcp || Port is > 0 and <= 65535);
}
