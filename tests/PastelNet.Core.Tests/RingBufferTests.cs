using PastelNet.Core.Metrics;
using Xunit;

namespace PastelNet.Core.Tests;

public class RingBufferTests
{
    [Fact]
    public void Starts_empty()
    {
        var buffer = new RingBuffer<int>(4);
        Assert.Equal(0, buffer.Count);
        Assert.Equal(4, buffer.Capacity);
        Assert.False(buffer.IsFull);
    }

    [Fact]
    public void Rejects_zero_capacity()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(0));

    [Fact]
    public void Keeps_insertion_order_until_full()
    {
        var buffer = new RingBuffer<int>(4);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.Equal(3, buffer.Count);
        Assert.Equal(1, buffer[0]);
        Assert.Equal(3, buffer[2]);
        Assert.Equal(3, buffer.Latest);
    }

    [Fact]
    public void Drops_oldest_when_overflowing()
    {
        var buffer = new RingBuffer<int>(3);
        for (int i = 1; i <= 5; i++)
            buffer.Add(i);

        Assert.Equal(3, buffer.Count);
        Assert.True(buffer.IsFull);
        Assert.Equal(3, buffer[0]);   // 1 と 2 は押し出された
        Assert.Equal(4, buffer[1]);
        Assert.Equal(5, buffer[2]);
        Assert.Equal(5, buffer.Latest);
    }

    [Fact]
    public void Survives_many_wraps()
    {
        var buffer = new RingBuffer<int>(3);
        for (int i = 0; i < 1000; i++)
            buffer.Add(i);

        Assert.Equal(997, buffer[0]);
        Assert.Equal(999, buffer.Latest);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Indexer_rejects_out_of_range(int index)
    {
        var buffer = new RingBuffer<int>(4);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[index]);
    }

    [Fact]
    public void CopyTo_writes_oldest_first()
    {
        var buffer = new RingBuffer<int>(3);
        for (int i = 1; i <= 5; i++)
            buffer.Add(i);

        Span<int> destination = stackalloc int[3];
        int written = buffer.CopyTo(destination);

        Assert.Equal(3, written);
        Assert.Equal(3, destination[0]);
        Assert.Equal(5, destination[2]);
    }

    [Fact]
    public void CopyTo_stops_at_destination_length()
    {
        var buffer = new RingBuffer<int>(5);
        for (int i = 1; i <= 5; i++)
            buffer.Add(i);

        Span<int> destination = stackalloc int[2];
        Assert.Equal(2, buffer.CopyTo(destination));
    }

    [Fact]
    public void CopyLatestTo_takes_the_newest_items()
    {
        var buffer = new RingBuffer<int>(10);
        for (int i = 1; i <= 8; i++)
            buffer.Add(i);

        Span<int> destination = stackalloc int[3];
        int written = buffer.CopyLatestTo(destination, 3);

        Assert.Equal(3, written);
        Assert.Equal(6, destination[0]);   // 古い順に並ぶ
        Assert.Equal(7, destination[1]);
        Assert.Equal(8, destination[2]);
    }

    [Fact]
    public void CopyLatestTo_handles_requests_larger_than_the_content()
    {
        var buffer = new RingBuffer<int>(10);
        buffer.Add(1);
        buffer.Add(2);

        Span<int> destination = stackalloc int[5];
        Assert.Equal(2, buffer.CopyLatestTo(destination, 5));
    }

    [Fact]
    public void Clear_resets_the_buffer()
    {
        var buffer = new RingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.False(buffer.IsFull);
    }
}
