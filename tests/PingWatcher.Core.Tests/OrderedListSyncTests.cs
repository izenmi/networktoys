using System.Collections.ObjectModel;
using System.Collections.Specialized;
using PingWatcher.Core.Net;
using Xunit;

namespace PingWatcher.Core.Tests;

public class OrderedListSyncTests
{
    private sealed record Row(string Key, string Value);

    private static Row R(string key, string value = "v") => new(key, value);

    private static ObservableCollection<Row> Collection(params Row[] rows) => new(rows);

    private static List<NotifyCollectionChangedAction> Watch(ObservableCollection<Row> collection)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions;
    }

    [Fact]
    public void Appending_only_adds()
    {
        var current = Collection(R("a"));
        var actions = Watch(current);

        OrderedListSync.Apply(current, [R("a"), R("b"), R("c")], r => r.Key);

        Assert.Equal(new[] { "a", "b", "c" }, current.Select(r => r.Key));
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Add, a));
    }

    [Fact]
    public void Removing_from_the_middle_only_removes()
    {
        var current = Collection(R("a"), R("b"), R("c"));
        var actions = Watch(current);

        OrderedListSync.Apply(current, [R("a"), R("c")], r => r.Key);

        Assert.Equal(new[] { "a", "c" }, current.Select(r => r.Key));
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Remove, a));
    }

    [Fact]
    public void Inserting_into_the_middle_only_adds()
    {
        var current = Collection(R("a"), R("c"));
        var actions = Watch(current);

        OrderedListSync.Apply(current, [R("a"), R("b"), R("c")], r => r.Key);

        Assert.Equal(new[] { "a", "b", "c" }, current.Select(r => r.Key));
        Assert.Single(actions);
        Assert.Equal(NotifyCollectionChangedAction.Add, actions[0]);
    }

    [Fact]
    public void Changed_content_with_the_same_key_becomes_a_replace()
    {
        var current = Collection(R("a"), R("b", "old"), R("c"));
        var actions = Watch(current);

        OrderedListSync.Apply(current, [R("a"), R("b", "new"), R("c")], r => r.Key);

        Assert.Equal("new", current[1].Value);
        Assert.Single(actions);
        Assert.Equal(NotifyCollectionChangedAction.Replace, actions[0]);
    }

    [Fact]
    public void Identical_lists_raise_no_notifications()
    {
        var current = Collection(R("a"), R("b"), R("c"));
        var actions = Watch(current);

        OrderedListSync.Apply(current, [R("a"), R("b"), R("c")], r => r.Key);

        Assert.Empty(actions);
    }

    [Fact]
    public void A_complete_swap_never_resets()
    {
        var current = Collection(R("a"), R("b"));
        var actions = Watch(current);

        OrderedListSync.Apply(current, [R("x"), R("y"), R("z")], r => r.Key);

        Assert.Equal(new[] { "x", "y", "z" }, current.Select(r => r.Key));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void Emptying_and_filling_both_work()
    {
        var current = Collection(R("a"), R("b"));
        OrderedListSync.Apply(current, [], r => r.Key);
        Assert.Empty(current);

        OrderedListSync.Apply(current, [R("a")], r => r.Key);
        Assert.Single(current);
    }

    [Fact]
    public void Interleaved_add_remove_and_replace_converge()
    {
        var current = Collection(R("a"), R("b", "old"), R("d"), R("e"));

        OrderedListSync.Apply(current, [R("b", "new"), R("c"), R("e"), R("f")], r => r.Key);

        Assert.Equal(new[] { "b", "c", "e", "f" }, current.Select(r => r.Key));
        Assert.Equal("new", current[0].Value);
    }
}
