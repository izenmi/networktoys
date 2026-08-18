using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NetworkToys.App.Mvvm;

/// <summary>
/// まとめて入れ替えられる <see cref="ObservableCollection{T}"/>。
///
/// 1 行ずつ <c>Add</c> すると、そのたびに一覧が測り直される。
/// 差分の 2 万行のように<b>一度に全部入れ替える</b>場面では、
/// 中身を差し替えてから「まるごと変わった」を 1 回だけ知らせる方が速い。
///
/// 逐次更新（Ping の測定結果など）には使わないこと — あちらは
/// 既存の行のプロパティを書き換える作りで、構造変化そのものが起きない。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void Reset(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();

        foreach (T item in items) Items.Add(item);

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
