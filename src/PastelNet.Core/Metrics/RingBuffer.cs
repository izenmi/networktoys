namespace PastelNet.Core.Metrics;

/// <summary>
/// 固定長のリングバッファ。容量を超えた分は古いものから捨てる。
///
/// 宛先ごとの測定履歴を持つのに使う。履歴を無制限に貯めると数百宛先で破綻するため、
/// 上限を決めて古い順に捨てるのが前提。
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;   // 最も古い要素の位置
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count => _count;

    public bool IsFull => _count == _items.Length;

    /// <summary>0 が最も古い要素。</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"要素数は {_count} です。");

            return _items[(_start + index) % _items.Length];
        }
    }

    /// <summary>最も新しい要素。空なら既定値。</summary>
    public T Latest => _count == 0 ? default! : this[_count - 1];

    public void Add(T item)
    {
        int position = (_start + _count) % _items.Length;
        _items[position] = item;

        if (_count == _items.Length)
            _start = (_start + 1) % _items.Length;
        else
            _count++;
    }

    /// <summary>古い順に書き出す。書き出した件数を返す。</summary>
    public int CopyTo(Span<T> destination)
    {
        int n = Math.Min(_count, destination.Length);
        for (int i = 0; i < n; i++)
            destination[i] = this[i];
        return n;
    }

    /// <summary>新しい方から <paramref name="count"/> 件を、古い順に書き出す。</summary>
    public int CopyLatestTo(Span<T> destination, int count)
    {
        int n = Math.Min(Math.Min(count, _count), destination.Length);
        int offset = _count - n;
        for (int i = 0; i < n; i++)
            destination[i] = this[offset + i];
        return n;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        _count = 0;
    }
}
