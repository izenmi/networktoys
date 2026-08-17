namespace NetworkToys.Core.Net;

/// <summary>プロキシの方式。</summary>
public enum ProxyMode
{
    /// <summary>プロキシを使わない(直接接続)。</summary>
    None,

    /// <summary>PAC(自動構成スクリプト)。</summary>
    Pac,

    /// <summary>固定のプロキシサーバ。</summary>
    Fixed,
}

/// <summary>
/// 検証済みのプロキシ設定。適用は Windows のユーザー設定
/// (WinINET)への書き込みで、管理者権限は要らない。
/// </summary>
public sealed record ProxyPlan(ProxyMode Mode, string PacUrl, string Server, string Bypass)
{
    /// <summary>画面の入力から組み立てる。error 非 null なら失敗(最初の 1 件のみ)。</summary>
    public static ProxyPlan? Parse(ProxyMode mode, string pacUrl, string server, string bypass, out string? error)
    {
        error = null;

        pacUrl = (pacUrl ?? "").Trim();
        server = (server ?? "").Trim();
        bypass = (bypass ?? "").Trim();

        switch (mode)
        {
            case ProxyMode.None:
                return new ProxyPlan(ProxyMode.None, "", "", "");

            case ProxyMode.Pac:
                if (pacUrl.Length == 0)
                {
                    error = "PAC の URL を入れてください(例: http://proxy.example.co.jp/proxy.pac)。";
                    return null;
                }

                if (!Uri.TryCreate(pacUrl, UriKind.Absolute, out Uri? uri)
                    || uri.Scheme is not ("http" or "https" or "file"))
                {
                    error = "PAC の URL の形式が正しくありません(http:// か https:// で始めてください)。";
                    return null;
                }

                return new ProxyPlan(ProxyMode.Pac, pacUrl, "", "");

            default:
                if (server.Length == 0)
                {
                    error = "プロキシサーバを入れてください(例: proxy.example.co.jp:8080)。";
                    return null;
                }

                if (server.Contains(' '))
                {
                    error = "プロキシサーバに空白は使えません。";
                    return null;
                }

                // "http=host:port;https=host:port" のプロトコル別指定はそのまま通す
                // (Windows の ProxyServer 値の書式)。単一指定のときだけポートを検査する
                if (!server.Contains('=') && server.Contains(':'))
                {
                    string portText = server[(server.LastIndexOf(':') + 1)..];
                    if (!int.TryParse(portText, out int port) || port is < 1 or > 65535)
                    {
                        error = "ポート番号は 1〜65535 で指定してください。";
                        return null;
                    }
                }

                return new ProxyPlan(ProxyMode.Fixed, "", server, bypass);
        }
    }
}
