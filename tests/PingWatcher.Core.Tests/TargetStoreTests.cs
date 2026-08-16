using PingWatcher.Core.Models;
using PingWatcher.Core.Storage;
using Xunit;

namespace PingWatcher.Core.Tests;

public class TargetStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pingwatcher-tests-" + Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_directory, name);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Missing_file_yields_an_empty_document()
    {
        TargetDocument document = TargetStore.Load(PathFor("nope.json"), out string? error);

        Assert.Null(error);
        Assert.Empty(document.Targets);
    }

    [Fact]
    public void Round_trips_targets_and_settings()
    {
        string path = PathFor("targets.json");
        var original = new TargetDocument
        {
            Targets =
            [
                new Target { Host = "192.168.1.1", Comment = "既定ゲートウェイ" },
                new Target { Host = "example.jp", Kind = ProbeKind.Tcp, Port = 443, Comment = "Web" },
            ],
            Settings = new MonitorSettings { IntervalMs = 2000, SlowThresholdMs = 250 },
        };

        TargetStore.Save(path, original);
        TargetDocument loaded = TargetStore.Load(path, out string? error);

        Assert.Null(error);
        Assert.Equal(2, loaded.Targets.Count);
        Assert.Equal("192.168.1.1", loaded.Targets[0].Host);
        Assert.Equal("既定ゲートウェイ", loaded.Targets[0].Comment);
        Assert.Equal(ProbeKind.Tcp, loaded.Targets[1].Kind);
        Assert.Equal(443, loaded.Targets[1].Port);
        Assert.Equal(2000, loaded.Settings.IntervalMs);
        Assert.Equal(250, loaded.Settings.SlowThresholdMs);
    }

    [Fact]
    public void Creates_missing_directories()
    {
        string path = Path.Combine(_directory, "nested", "deeper", "targets.json");
        TargetStore.Save(path, new TargetDocument());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Broken_json_does_not_throw()
    {
        string path = PathFor("broken.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ this is not json");

        TargetDocument document = TargetStore.Load(path, out string? error);

        Assert.NotNull(error);
        Assert.Empty(document.Targets);
    }

    [Fact]
    public void Invalid_targets_are_dropped_on_load()
    {
        string path = PathFor("targets.json");
        TargetStore.Save(path, new TargetDocument
        {
            Targets =
            [
                new Target { Host = "ok.example.jp" },
                new Target { Host = "" },                                        // ホスト名が空
                new Target { Host = "tcp.example.jp", Kind = ProbeKind.Tcp },     // ポート未指定
            ],
        });

        TargetDocument loaded = TargetStore.Load(path);

        Assert.Single(loaded.Targets);
        Assert.Equal("ok.example.jp", loaded.Targets[0].Host);
    }

    [Fact]
    public void Out_of_range_settings_are_clamped_on_load()
    {
        string path = PathFor("targets.json");
        TargetStore.Save(path, new TargetDocument
        {
            Settings = new MonitorSettings { IntervalMs = 1, MaxConcurrency = 100_000, HistoryLength = 0 },
        });

        MonitorSettings settings = TargetStore.Load(path).Settings;

        Assert.Equal(100, settings.IntervalMs);
        Assert.Equal(1024, settings.MaxConcurrency);
        Assert.Equal(30, settings.HistoryLength);
    }

    [Fact]
    public void Save_replaces_the_previous_file_without_leaving_temporaries()
    {
        string path = PathFor("targets.json");
        TargetStore.Save(path, new TargetDocument { Targets = [new Target { Host = "first.example.jp" }] });
        TargetStore.Save(path, new TargetDocument { Targets = [new Target { Host = "second.example.jp" }] });

        Assert.Equal("second.example.jp", TargetStore.Load(path).Targets[0].Host);
        Assert.False(File.Exists(path + ".tmp"));
    }
}
