namespace NetworkToys.Core.Design;

/// <summary>
/// 画面での目立たせ方。色だけで表さない決まりなので、文字の側は必ず記号と言葉を伴う。
///
/// 接続タブの <c>ConnectionStateKind</c> とは別に持つ — あちらに「危険」が無く、
/// 足すと既存 3 タブの分岐に手を入れることになる。
///
/// <b>XAML の <c>DataTrigger</c> は値の名前（"Ok" / "Alert"）で書いてある。</b>
/// 並びや名前を変えると、画面の色分けが黙って効かなくなる。
/// </summary>
public enum SeverityKind
{
    /// <summary>情報が無い・対象外。</summary>
    Muted,

    /// <summary>正常。</summary>
    Ok,

    /// <summary>気に留める程度。</summary>
    Notice,

    /// <summary>すぐ見るべきもの。</summary>
    Alert,
}
