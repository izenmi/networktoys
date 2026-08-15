using PingWatcher.Core.Models;
using PingWatcher.Core.Storage;
using Xunit;

namespace PingWatcher.Core.Tests;

public class AppSettingsStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"pingwatcher-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void Settings_round_trip_through_json()
    {
        string path = TempPath();
        try
        {
            var document = new AppSettingsDocument
            {
                Theme = "light",
                Columns = [84, 156, 76, 66, 110],
            };
            document.Ping.Targets.Add(new Target { Host = "192.168.1.1", Comment = "GW" });
            document.Tcp.Targets.Add(new Target { Host = "srv-01", Port = 445 });

            AppSettingsStore.Save(path, document);
            AppSettingsDocument loaded = AppSettingsStore.Load(path, out string? error);

            Assert.Null(error);
            Assert.Equal("light", loaded.Theme);
            Assert.Equal(new double[] { 84, 156, 76, 66, 110 }, loaded.Columns);
            Assert.Equal("192.168.1.1", Assert.Single(loaded.Ping.Targets).Host);
            Assert.Equal(445, Assert.Single(loaded.Tcp.Targets).Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_yields_defaults_without_error()
    {
        AppSettingsDocument loaded = AppSettingsStore.Load(TempPath(), out string? error);

        Assert.Null(error);
        Assert.Equal("dark", loaded.Theme);
        Assert.Empty(loaded.Columns);
        Assert.Empty(loaded.Ping.Targets);
    }

    [Fact]
    public void Broken_json_reports_an_error_and_keeps_defaults()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ broken");
            AppSettingsDocument loaded = AppSettingsStore.Load(path, out string? error);

            Assert.NotNull(error);
            Assert.Equal("dark", loaded.Theme);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unknown_theme_values_fall_back_to_dark()
    {
        string path = TempPath();
        try
        {
            AppSettingsStore.Save(path, new AppSettingsDocument { Theme = "hotpink" });
            AppSettingsDocument loaded = AppSettingsStore.Load(path, out _);

            Assert.Equal("dark", loaded.Theme);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Invalid_targets_are_dropped_on_load()
    {
        string path = TempPath();
        try
        {
            var document = new AppSettingsDocument();
            document.Ping.Targets.Add(new Target { Host = "" });          // 無効
            document.Ping.Targets.Add(new Target { Host = "10.0.0.1" });  // 有効

            AppSettingsStore.Save(path, document);
            AppSettingsDocument loaded = AppSettingsStore.Load(path, out _);

            Assert.Equal("10.0.0.1", Assert.Single(loaded.Ping.Targets).Host);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
