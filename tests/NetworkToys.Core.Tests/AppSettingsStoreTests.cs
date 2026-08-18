using NetworkToys.Core.Models;
using NetworkToys.Core.Storage;
using Xunit;

namespace NetworkToys.Core.Tests;

public class AppSettingsStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"networktoys-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void Settings_round_trip_through_json()
    {
        string path = TempPath();
        try
        {
            var document = new AppSettingsDocument
            {
                Theme = "light",
            };
            document.Ping.Targets.Add(new Target { Host = "192.168.1.1", Comment = "GW" });
            document.Tcp.Targets.Add(new Target { Host = "srv-01", Port = 445 });

            AppSettingsStore.Save(path, document);
            AppSettingsDocument loaded = AppSettingsStore.Load(path, out string? error);

            Assert.Null(error);
            Assert.Equal("light", loaded.Theme);
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
        Assert.Equal("light", loaded.Theme);
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
            Assert.Equal("light", loaded.Theme);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unknown_theme_values_fall_back_to_light()
    {
        string path = TempPath();
        try
        {
            AppSettingsStore.Save(path, new AppSettingsDocument { Theme = "hotpink" });
            AppSettingsDocument loaded = AppSettingsStore.Load(path, out _);

            Assert.Equal("light", loaded.Theme);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ip_presets_round_trip_and_nameless_ones_are_dropped()
    {
        string path = TempPath();
        try
        {
            var document = new AppSettingsDocument();
            document.IpPresets.Add(new IpPreset
            {
                Name = "現場A",
                Address = "192.168.10.50",
                Mask = "24",
                Gateway = "192.168.10.1",
                Dns1 = "192.168.10.1",
                Dns2 = "8.8.8.8",
            });
            document.IpPresets.Add(new IpPreset { Name = "事務所", Dhcp = true });
            document.IpPresets.Add(new IpPreset { Name = "  " });   // 名前なし → 捨てる

            AppSettingsStore.Save(path, document);
            AppSettingsDocument loaded = AppSettingsStore.Load(path, out _);

            Assert.Equal(2, loaded.IpPresets.Count);
            IpPreset siteA = loaded.IpPresets[0];
            Assert.Equal("現場A", siteA.Name);
            Assert.Equal("192.168.10.50", siteA.Address);
            Assert.Equal("24", siteA.Mask);
            Assert.Equal("192.168.10.1", siteA.Gateway);
            Assert.Equal("8.8.8.8", siteA.Dns2);
            Assert.True(loaded.IpPresets[1].Dhcp);
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
