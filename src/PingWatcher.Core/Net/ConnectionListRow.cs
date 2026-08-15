namespace PingWatcher.Core.Net;

/// <summary>
/// 接続タブに表示する行。プロセス見出し行と接続行を 1 本のフラットな一覧に混ぜ、
/// App 側は型ごとの DataTemplate で出し分ける（CollectionView の GroupStyle は
/// 毎ティックの再グループ化が実質 Refresh になるため使わない）。
///
/// record（不変）なので変更通知は持たず、更新は <see cref="OrderedListSync"/> の
/// 同位置置換で行う。SortKey は差分同期の同一性キーで、一覧の中で一意。
/// </summary>
public abstract record ConnectionListRow(string SortKey);

/// <summary>プロセスの見出し行。送受信は配下の接続の合計。</summary>
public sealed record ConnectionGroupRow(
    string ProcessName,
    string PidText,
    string CountText,
    string SentText,
    string ReceivedText,
    string SortKey) : ConnectionListRow(SortKey);

/// <summary>接続 1 本の行。すべて表示文字列に整形済み。</summary>
public sealed record ConnectionDetailRow(
    string Protocol,
    string Local,
    string Remote,
    string StateText,
    ConnectionStateKind StateKind,
    string SentText,
    string ReceivedText,
    string SortKey) : ConnectionListRow(SortKey);
