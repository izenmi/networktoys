using System.Text;

namespace NetworkToys.Core.Verify;

/// <summary>プロキシの指定のしかた。</summary>
public enum ProxyMode
{
    /// <summary>プロキシを通さず直接出る。</summary>
    Direct,

    /// <summary>アドレスを直に指定する（<c>http://10.0.0.10:8080</c>）。</summary>
    Fixed,

    /// <summary>
    /// PAC の URL を指定する。<b>宛先 URL ごとに評価が変わる</b>ので、
    /// 実際に使うプロキシは試験のたびに解決する。
    /// </summary>
    Pac,

    /// <summary>いまの Windows の設定に従う。比較の基準として使う。</summary>
    System,
}

/// <summary>
/// 試験に使うプロキシ 1 件。
/// </summary>
/// <param name="Name">画面と証跡に出す名前。</param>
/// <param name="Mode">指定のしかた。</param>
/// <param name="Address">
/// <see cref="ProxyMode.Fixed"/> ならプロキシのアドレス、
/// <see cref="ProxyMode.Pac"/> なら PAC の URL。ほかは空。
/// </param>
public sealed record ProxyChoice(string Name, ProxyMode Mode, string Address)
{
    /// <summary>
    /// 一覧や証跡に出す短い名前。
    ///
    /// 名前を省いて<b>アドレスだけ書かれたとき、名前はアドレスそのもの</b>になる。
    /// PAC の URL は長いので、<b>ファイル名だけ</b>にする（2026-08-18 ユーザー指示）。
    /// 同じ名前のファイルが複数あるときのために、ホスト名を添える。
    /// </summary>
    public string ShortName
    {
        get
        {
            if (!Name.Contains("://", StringComparison.Ordinal)) return Name;
            if (!Uri.TryCreate(Name, UriKind.Absolute, out Uri? uri)) return Name;

            string file = uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : "";

            return file.Length > 0 ? $"{file}（{uri.Host}）" : uri.Host;
        }
    }

    /// <summary>常に選べる「直接」。プロキシ無しの結果と比べるために要る。</summary>
    public static ProxyChoice Direct { get; } = new("直接", ProxyMode.Direct, "");

    /// <summary>
    /// 常に選べる「Windows のプロキシ設定」。
    ///
    /// 名前が「いまの設定」では<b>何の設定なのか伝わらない</b>ので、どこの設定かを名前に入れる。
    /// 指すのは Windows の「プロキシ サーバーを使う」／自動構成スクリプト
    /// （＝ブラウザが従うのと同じ設定。IP設定タブの下段で変えられる）。
    /// </summary>
    public static ProxyChoice System { get; } = new("Windows のプロキシ設定", ProxyMode.System, "");

    /// <summary>画面に出す説明。<b>それが何を指すのか</b>まで書く。</summary>
    public string Summary => Mode switch
    {
        ProxyMode.Direct => "プロキシを通さず直接出ます。プロキシ側の問題かどうかを切り分ける基準です。",
        ProxyMode.System => "この PC にいま入っている Windows のプロキシ設定に従います"
                          + "（ブラウザが従うのと同じ設定。IP設定タブの下段で確認・変更できます）。",
        ProxyMode.Pac => $"自動構成スクリプト（PAC）で決めます: {Address}",
        _ => $"このプロキシを通します: {Address}",
    };
}

/// <summary>
/// プロキシの一覧を、設定ファイルに置けるテキストと相互に変換する。
///
/// 書式は <c>名前,種類,アドレス</c> の 1 行 1 件。行頭 <c>#</c> は注釈。
/// <b>「直接」と「いまの設定」は常に先頭に在る</b>ので、テキストには書かない。
///
/// 種類を書式に明示しているのは、PAC の URL と固定プロキシのアドレスが
/// どちらも <c>http://…</c> で始まり、見た目で区別できないため。
/// </summary>
public static class ProxyListParser
{
    /// <summary>
    /// 解釈する。<b>先頭は必ず「直接」と「いまの設定」。</b>
    /// 名前が重なったものは後ろを捨てる（証跡に同名が並ぶとどちらの結果か分からなくなる）。
    /// </summary>
    public static IReadOnlyList<ProxyChoice> Parse(string? text)
    {
        List<ProxyChoice> list = [ProxyChoice.Direct, ProxyChoice.System];
        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase)
        {
            ProxyChoice.Direct.Name,
            ProxyChoice.System.Name,
        };

        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (string raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

            string[] parts = line.Replace('\t', ',').Split(',', 3);

            // 名前と種類を省いて、アドレスだけ書かれることがある（2026-08-18 報告）。
            // 「.pac で終わる URL は PAC、それ以外はプロキシ」と読んで受ける —
            // 弾いて一覧に出ないと、直接しか選べないまま原因も分からない
            if (parts.Length < 3)
            {
                if (!TryParseBare(line, out ProxyChoice bare) || !known.Add(bare.Name)) continue;

                list.Add(bare);
                continue;
            }

            string name = parts[0].Trim();
            if (name.Length == 0 || !TryParseMode(parts[1], out ProxyMode mode)) continue;

            string address = mode switch
            {
                ProxyMode.Fixed => NormalizeProxy(parts[2]),
                ProxyMode.Pac => parts[2].Trim(),
                _ => "",
            };

            // アドレスの要る種類でアドレスが無ければ、直接と区別が付かない。捨てる
            if (mode is ProxyMode.Fixed or ProxyMode.Pac && address.Length == 0) continue;
            if (!known.Add(name)) continue;

            list.Add(new ProxyChoice(name, mode, address));
        }

        return list;
    }

    public static string Format(IEnumerable<ProxyChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        var text = new StringBuilder();

        foreach (ProxyChoice choice in choices)
        {
            // 常にある 2 つは書き戻さない
            if (choice.Mode is ProxyMode.Direct or ProxyMode.System) continue;

            text.Append(choice.Name).Append(',')
                .Append(NameOf(choice.Mode)).Append(',')
                .Append(choice.Address).Append('\n');
        }

        return text.ToString();
    }

    public static string NameOf(ProxyMode mode) => mode switch
    {
        ProxyMode.Direct => "direct",
        ProxyMode.Pac => "pac",
        ProxyMode.System => "system",
        _ => "proxy",
    };

    /// <summary>
    /// アドレスだけの 1 行を読む。<b>名前はアドレスそのもの</b>にする
    /// （名前を勝手に付けると、証跡でどれか分からなくなる）。
    /// <c>.pac</c> で終わる URL は PAC、それ以外はプロキシのアドレスとみなす。
    /// </summary>
    public static bool TryParseBare(string? text, out ProxyChoice choice)
    {
        choice = ProxyChoice.Direct;

        string line = (text ?? "").Trim();

        if (line.Length == 0 || line.Contains(',', StringComparison.Ordinal)) return false;
        if (line.Contains(' ', StringComparison.Ordinal)) return false;

        // ただの語を拾わない。アドレスなら「.」か「:」のどちらかは必ずある
        // （"名前だけ" を http://名前だけ として受けてしまっていた）
        if (!line.Contains('.', StringComparison.Ordinal)
            && !line.Contains(':', StringComparison.Ordinal))
            return false;

        bool looksLikePac = line.EndsWith(".pac", StringComparison.OrdinalIgnoreCase)
                            || line.Contains(".pac?", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("/proxy.pac", StringComparison.OrdinalIgnoreCase);

        if (looksLikePac)
        {
            choice = new ProxyChoice(line, ProxyMode.Pac, line);
            return true;
        }

        // ホスト:ポート か URL に見えるものだけ受ける（ただの語を拾わない）
        string address = NormalizeProxy(line);

        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
            return false;

        choice = new ProxyChoice(line, ProxyMode.Fixed, address);
        return true;
    }

    public static bool TryParseMode(string? text, out ProxyMode mode)
    {
        mode = ProxyMode.Fixed;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToUpperInvariant())
        {
            case "DIRECT": case "直接": mode = ProxyMode.Direct; return true;
            case "PAC": mode = ProxyMode.Pac; return true;
            case "SYSTEM": case "WINDOWS": mode = ProxyMode.System; return true;
            case "PROXY": case "FIXED": mode = ProxyMode.Fixed; return true;
            default: return false;
        }
    }

    /// <summary>
    /// <c>10.0.0.10:8080</c> のように書かれても通るよう <c>http://</c> を補う。
    /// <b>プロキシへの繋ぎ方は http のままでよい</b>（https のサイトも CONNECT で中継される）。
    /// </summary>
    public static string NormalizeProxy(string? address)
    {
        string text = (address ?? "").Trim();

        if (text.Length == 0) return "";

        return text.Contains("://", StringComparison.Ordinal) ? text : "http://" + text;
    }
}
