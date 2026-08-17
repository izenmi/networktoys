using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using NetworkToys.Core.Net;

namespace NetworkToys.App.Services;

/// <summary>
/// 証明書を受け入れていないので繋がない、という失敗。<see cref="Fingerprint"/> を画面に出して
/// 人に見比べてもらうために、指紋を持って投げる。
/// </summary>
internal sealed class PinnedCertificateException(string message, string fingerprint) : Exception(message)
{
    public string Fingerprint { get; } = fingerprint;
}

/// <summary>
/// 自己署名の証明書を「指紋で見分ける」ための持ち物。APIC も C9800 も、
/// 社内の機器は自己署名が普通なので、SSH のホスト鍵と同じ考え方で通す。
///
/// <b>検証は切らない。</b>正規の証明書はそのまま通し、そうでないものは
/// <b>人が受け入れた指紋と一致したときだけ</b>通す。
///
/// <b>ここでは人に聞かない。</b>握手の途中でモーダルを出すと UI スレッドと握手スレッドで詰む。
/// 断ったときも指紋を控えておき（出せなければ人は受け入れようがない）、
/// 画面側が <see cref="PinnedCertificateException"/> を受けてから聞く。
///
/// <b>この中身は CI では 1 度も実行されない</b> — 自己診断は偽のハンドラを挿すので TLS を通らない。
/// 触ったら実機で確かめること。
/// </summary>
internal sealed class PinnedCertificate(string? accepted)
{
    /// <summary>
    /// 相手が出した指紋。検証はリクエストのスレッドから呼ばれるので volatile。
    /// </summary>
    private volatile string _seen = "";

    public string Seen => _seen;

    public HttpClientHandler CreateHandler() => new()
    {
        // http へ落とす 302 に黙って従わない（相手が目的の機器でないかもしれない）
        AllowAutoRedirect = false,

        ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null) return false;

            _seen = HttpsHost.Fingerprint(certificate.GetCertHash(HashAlgorithmName.SHA256));

            if (errors == SslPolicyErrors.None) return true;

            return _seen.Length > 0 && string.Equals(_seen, accepted, StringComparison.Ordinal);
        },
    };

    /// <summary>
    /// TLS で断られたのかを見分ける。握手の失敗は
    /// <see cref="AuthenticationException"/> を内側に持つ形で出てくる。
    /// 検証コールバックが指紋を控えていれば、それも根拠になる。
    /// </summary>
    public bool IsProblem(HttpRequestException ex)
        => ex is not null
           && (ex.InnerException is AuthenticationException
               || (_seen.Length > 0 && !string.Equals(_seen, accepted, StringComparison.Ordinal)));

    /// <summary>控えた指紋を添えて投げる。</summary>
    public PinnedCertificateException Refused()
        => new("証明書を確認できませんでした。", _seen);
}
