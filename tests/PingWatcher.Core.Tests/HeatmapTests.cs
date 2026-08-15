using PingWatcher.Core.Survey;
using Xunit;

namespace PingWatcher.Core.Tests;

public class HeatmapTests
{
    private static List<SurveyReading> Readings() =>
    [
        new SurveyReading { Ssid = "office", Bssid = "AA-BB-CC-11-22-33", Rssi = -52 },
        new SurveyReading { Ssid = "guest", Bssid = "AA-BB-CC-44-55-66", Rssi = -71 },
    ];

    [Fact]
    public void Connected_mode_reads_the_connected_bssid()
    {
        double? value = Heatmap.SelectValue(Readings(), "aa-bb-cc-11-22-33",
            new HeatmapSource(HeatmapMode.Connected, null));

        Assert.Equal(-52, value);
    }

    [Fact]
    public void Connected_mode_without_a_connection_yields_nothing()
    {
        Assert.Null(Heatmap.SelectValue(Readings(), null, new HeatmapSource(HeatmapMode.Connected, null)));
        Assert.Null(Heatmap.SelectValue(Readings(), "FF-FF-FF-00-00-00", new HeatmapSource(HeatmapMode.Connected, null)));
    }

    [Fact]
    public void Single_bssid_mode_reads_that_ap_only()
    {
        var source = new HeatmapSource(HeatmapMode.SingleBssid, "AA-BB-CC-44-55-66");

        Assert.Equal(-71, Heatmap.SelectValue(Readings(), null, source));
        Assert.Null(Heatmap.SelectValue(Readings(), null, new HeatmapSource(HeatmapMode.SingleBssid, "00-00-00-00-00-00")));
        Assert.Null(Heatmap.SelectValue(Readings(), null, new HeatmapSource(HeatmapMode.SingleBssid, null)));
    }

    [Fact]
    public void Strongest_mode_takes_the_maximum()
    {
        Assert.Equal(-52, Heatmap.SelectValue(Readings(), null, new HeatmapSource(HeatmapMode.Strongest, null)));
        Assert.Null(Heatmap.SelectValue([], null, new HeatmapSource(HeatmapMode.Strongest, null)));
    }

    [Fact]
    public void A_single_sample_paints_its_own_cell_with_its_value()
    {
        SamplePoint[] samples = [new(0.5, 0.5, -60)];

        float[] grid = Heatmap.Interpolate(samples, 4, 4, radius: 0.2);

        // 中央 4 セルのどれかは半径内。値は必ず -60 のまま(IDW は実測値を超えない)
        float center = grid[1 * 4 + 1];
        Assert.False(float.IsNaN(center));
        Assert.Equal(-60, center, 1);
    }

    [Fact]
    public void Cells_outside_the_radius_stay_unpainted()
    {
        SamplePoint[] samples = [new(0.0, 0.0, -60)];

        float[] grid = Heatmap.Interpolate(samples, 8, 8, radius: 0.1);

        Assert.True(float.IsNaN(grid[^1]));   // 右下の角は半径 0.1 の外
        Assert.False(float.IsNaN(grid[0]));   // 左上の角は中
    }

    [Fact]
    public void The_midpoint_of_two_samples_is_their_average()
    {
        SamplePoint[] samples = [new(0.25, 0.5, -50), new(0.75, 0.5, -70)];

        // 2x2 グリッドは存在しないため 1 セル幅が中点に乗る奇数グリッドで確かめる
        float[] grid = Heatmap.Interpolate(samples, 2, 1, radius: 1.0);

        // セル中心 (0.25,0.5) と (0.75,0.5) はそれぞれ測定点そのもの
        Assert.Equal(-50, grid[0], 1);
        Assert.Equal(-70, grid[1], 1);

        float[] mid = Heatmap.Interpolate(samples, 1, 1, radius: 1.0);
        Assert.Equal(-60, mid[0], 1);   // 中央 (0.5,0.5) は等距離 → 平均
    }

    [Theory]
    [InlineData(-95, 0)]
    [InlineData(-85, 1)]
    [InlineData(-80, 1)]
    [InlineData(-75, 2)]
    [InlineData(-65, 3)]
    [InlineData(-55, 4)]
    [InlineData(-45, 5)]
    [InlineData(-30, 5)]
    public void Band_boundaries_are_stable(double rssi, int expected)
    {
        Assert.Equal(expected, Heatmap.BandIndex(rssi));
    }

    [Fact]
    public void Fit_rect_letterboxes_both_ways()
    {
        // 横長の枠に 4:3 → 高さいっぱい・左右に余白
        (double x, double y, double w, double h) = Heatmap.FitRect(4.0 / 3.0, 800, 300);
        Assert.Equal(300, h, 3);
        Assert.Equal(400, w, 3);
        Assert.Equal(200, x, 3);
        Assert.Equal(0, y, 3);

        // 縦長の枠に 4:3 → 幅いっぱい・上下に余白
        (x, y, w, h) = Heatmap.FitRect(4.0 / 3.0, 400, 600);
        Assert.Equal(400, w, 3);
        Assert.Equal(300, h, 3);
        Assert.Equal(0, x, 3);
        Assert.Equal(150, y, 3);

        // ぴったり一致
        (x, y, w, h) = Heatmap.FitRect(2.0, 200, 100);
        Assert.Equal((0d, 0d, 200d, 100d), (x, y, w, h));

        // 不正値は空
        Assert.Equal((0d, 0d, 0d, 0d), Heatmap.FitRect(0, 100, 100));
    }
}
