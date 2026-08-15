namespace PingWatcher.Core.Net;

/// <summary>
/// 表示中の一覧を目標の一覧へ、最小の挿入・削除・同位置置換で変形する。
/// Clear + 全 Add は Reset 通知で行コンテナが全再生成され、毎ティックの
/// ちらつき・選択消失・スクロール位置の不安定化を招くので使わない。
///
/// 前提: 両方の一覧が同じ決定的な全順序で並んでいる（共通要素の相対順が同じ）。
/// desired のキーは一意であること。
/// </summary>
public static class OrderedListSync
{
    public static void Apply<T>(IList<T> current, IReadOnlyList<T> desired, Func<T, string> keyOf)
    {
        var desiredKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (T item in desired)
            desiredKeys.Add(keyOf(item));

        int index = 0;
        foreach (T item in desired)
        {
            string key = keyOf(item);

            // 目標に無い行が挟まっていたら消えた行なので取り除く。
            // 目標に「ある」行に当たったら止める（それはこの行より後で使う）
            while (index < current.Count)
            {
                string currentKey = keyOf(current[index]);
                if (currentKey == key || desiredKeys.Contains(currentKey))
                    break;
                current.RemoveAt(index);
            }

            if (index < current.Count && keyOf(current[index]) == key)
            {
                // 同じ行が内容だけ変わった（状態遷移・件数・レート）なら同位置置換。
                // 完全一致なら触らない（コンテナ再利用がそのまま効く）
                if (!EqualityComparer<T>.Default.Equals(current[index], item))
                    current[index] = item;
            }
            else
            {
                current.Insert(index, item);
            }

            index++;
        }

        while (current.Count > index)
            current.RemoveAt(current.Count - 1);
    }
}
