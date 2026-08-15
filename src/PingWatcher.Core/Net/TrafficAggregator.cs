using System.Runtime.InteropServices;

namespace PingWatcher.Core.Net;

/// <summary>
/// ETW スレッドから積まれる通信イベントをフロー別に集計する。
/// <see cref="Add"/> は ETW の処理スレッドから、<see cref="Drain"/> は UI 側の
/// ティックから呼ばれるので lock で守る（イベントごとの競合は短いので十分軽い）。
/// </summary>
public sealed class TrafficAggregator
{
    private readonly object _gate = new();
    private Dictionary<FlowKey, FlowTotals> _flows = [];

    public void Add(in FlowKey key, bool sent, uint bytes)
    {
        lock (_gate)
        {
            ref FlowTotals totals = ref CollectionsMarshal.GetValueRefOrAddDefault(_flows, key, out _);
            if (sent)
                totals.Sent += bytes;
            else
                totals.Received += bytes;
        }
    }

    /// <summary>集計を取り出して空に戻す。次の窓はゼロから数え直す。</summary>
    public Dictionary<FlowKey, FlowTotals> Drain()
    {
        lock (_gate)
        {
            Dictionary<FlowKey, FlowTotals> drained = _flows;
            _flows = [];
            return drained;
        }
    }
}
