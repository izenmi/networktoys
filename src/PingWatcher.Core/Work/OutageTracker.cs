using PingWatcher.Core.Models;

namespace PingWatcher.Core.Work;

/// <summary>
/// 宛先ごとの不通を、サンプルを見ながら記録していく。
///
/// 定義:
/// ・<b>開くのは連続失敗が 2 回に達した時点</b>（一覧の振り分けと揃える）。
///   ただし<b>開始時刻は最後に応答があった時刻まで遡らせる</b>ので、
///   持続時間は「本当に切れていた長さ」になる。
/// ・閉じるのは応答が戻った時。
/// ・<b>1 回きりの失敗は不通として記録しない。</b>そうしないと、無線や混んだ回線で
///   1〜2 秒の「障害」が並び、本命の切替が埋もれる。回数だけ別に数える。
///
/// UI にもネットワークにも触らないので、合成したサンプル列でテストできる。
/// </summary>
public sealed class OutageTracker
{
    private sealed class Entry
    {
        public required string Host { get; init; }
        public ProbeStateMachine Machine { get; } = new();
        public long? OpenedFromTicks { get; set; }
        public bool StartUnknown { get; set; }
        public int MissedProbes { get; set; }
        public Dictionary<ProbeStatus, int> StatusCounts { get; } = [];
        public OutageRecord? Current { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OutageRecord> _records = [];
    private readonly int _maxRecords;
    private readonly int _intervalMs;

    /// <param name="intervalMs">測定間隔。記録に残して誤差の幅を示すのに使う。</param>
    /// <param name="maxRecords">保持する上限。ばたついたときに際限なく増えないようにする。</param>
    public OutageTracker(int intervalMs, int maxRecords = 500)
    {
        _intervalMs = Math.Max(1, intervalMs);
        _maxRecords = Math.Max(1, maxRecords);
    }

    /// <summary>記録された不通。新しいものが先。</summary>
    public IReadOnlyList<OutageRecord> Records => _records;

    /// <summary>上限を超えて捨てた件数。</summary>
    public int DroppedRecords { get; private set; }

    /// <summary>いま不通が続いている宛先の数。</summary>
    public int OngoingCount => _entries.Values.Count(e => e.Current is not null);

    /// <summary>
    /// サンプルを 1 件取り込む。不通が始まったか終わったときに、その記録を返す。
    /// </summary>
    public OutageRecord? Observe(string targetKey, string host, in ProbeSample sample)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetKey);

        if (!_entries.TryGetValue(targetKey, out Entry? entry))
        {
            entry = new Entry { Host = host };
            _entries[targetKey] = entry;
        }

        bool wasDown = entry.Machine.State == LinkState.Down;
        entry.Machine.Observe(sample);
        bool isDown = entry.Machine.State == LinkState.Down;

        if (sample.Status is not (ProbeStatus.Success or ProbeStatus.Refused or ProbeStatus.Pending))
        {
            entry.MissedProbes++;
            entry.StatusCounts[sample.Status] = entry.StatusCounts.GetValueOrDefault(sample.Status) + 1;
        }

        // 落ちた
        if (isDown && !wasDown)
            return Open(targetKey, entry, sample);

        // 戻った
        if (!isDown && entry.Current is not null)
            return Close(targetKey, entry, sample.TimestampTicks, OutageCloseReason.Recovered);

        return null;
    }

    private OutageRecord Open(string targetKey, Entry entry, in ProbeSample sample)
    {
        // 最後に応答があった時刻まで遡る。一度も応答が無ければ、その失敗の時刻で代用する
        bool startUnknown = entry.Machine.LastSuccessTicks is null;
        long startedAt = entry.Machine.LastSuccessTicks ?? sample.TimestampTicks;

        entry.OpenedFromTicks = startedAt;
        entry.StartUnknown = startUnknown;

        var record = new OutageRecord(
            targetKey,
            entry.Host,
            startedAt,
            null,
            entry.MissedProbes,
            _intervalMs,
            OutageCloseReason.Ongoing,
            Dominant(entry),
            startUnknown);

        entry.Current = record;
        Insert(record);
        return record;
    }

    private OutageRecord Close(string targetKey, Entry entry, long endedAtTicks, OutageCloseReason reason)
    {
        OutageRecord open = entry.Current!;

        var closed = open with
        {
            EndedAtTicks = endedAtTicks,
            MissedProbes = entry.MissedProbes,
            CloseReason = reason,
            DominantStatus = Dominant(entry),
        };

        int index = _records.IndexOf(open);
        if (index >= 0)
            _records[index] = closed;
        else
            Insert(closed);

        entry.Current = null;
        entry.OpenedFromTicks = null;
        entry.MissedProbes = 0;
        entry.StatusCounts.Clear();

        return closed;
    }

    /// <summary>測定を止めた、または宛先が消えたときに、続いている記録を閉じる。</summary>
    public IReadOnlyList<OutageRecord> CloseAll(long nowTicks, OutageCloseReason reason)
    {
        var closed = new List<OutageRecord>();

        foreach ((string key, Entry entry) in _entries)
        {
            if (entry.Current is not null)
                closed.Add(Close(key, entry, nowTicks, reason));
        }

        return closed;
    }

    /// <summary>宛先が一覧から消えたとき。復旧と混同しないよう理由を残して閉じる。</summary>
    public OutageRecord? Remove(string targetKey, long nowTicks)
    {
        if (!_entries.TryGetValue(targetKey, out Entry? entry))
            return null;

        OutageRecord? closed = entry.Current is not null
            ? Close(targetKey, entry, nowTicks, OutageCloseReason.Removed)
            : null;

        _entries.Remove(targetKey);
        return closed;
    }

    /// <summary>その宛先の状態機械。統計や表示に使う。</summary>
    public ProbeStateMachine? StateOf(string targetKey)
        => _entries.TryGetValue(targetKey, out Entry? entry) ? entry.Machine : null;

    /// <summary>指定した期間に不通があった宛先。作業前後の比較で「途中で切れた」を拾うのに使う。</summary>
    public IReadOnlyCollection<string> KeysWithOutageBetween(long fromTicks, long toTicks)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (OutageRecord record in _records)
        {
            long start = record.StartedAtTicks;
            long end = record.EndedAtTicks ?? toTicks;

            // 期間と少しでも重なっていれば拾う
            if (start <= toTicks && end >= fromTicks)
                keys.Add(record.TargetKey);
        }

        return keys;
    }

    public void Clear()
    {
        _entries.Clear();
        _records.Clear();
        DroppedRecords = 0;
    }

    private void Insert(OutageRecord record)
    {
        _records.Insert(0, record);

        while (_records.Count > _maxRecords)
        {
            _records.RemoveAt(_records.Count - 1);
            DroppedRecords++;
        }
    }

    private static ProbeStatus Dominant(Entry entry)
    {
        ProbeStatus dominant = ProbeStatus.TimedOut;
        int best = 0;

        foreach ((ProbeStatus status, int count) in entry.StatusCounts)
        {
            if (count > best)
            {
                best = count;
                dominant = status;
            }
        }

        return dominant;
    }
}
