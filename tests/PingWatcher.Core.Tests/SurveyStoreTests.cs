using PingWatcher.Core.Survey;
using Xunit;

namespace PingWatcher.Core.Tests;

public class SurveyStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"pingwatcher-survey-{Guid.NewGuid():N}.json");

    private static SurveyDocument Sample() => new()
    {
        Name = "3F 事務所",
        CreatedAt = new DateTimeOffset(2026, 8, 15, 10, 30, 0, TimeSpan.FromHours(9)),
        FloorImageFile = "20260815-1030-floor.png",
        AspectRatio = 1.6,
        Points =
        [
            new SurveyPoint
            {
                X = 0.25,
                Y = 0.75,
                MeasuredAt = new DateTimeOffset(2026, 8, 15, 10, 31, 0, TimeSpan.FromHours(9)),
                ConnectedBssid = "AA-BB-CC-11-22-33",
                Readings =
                [
                    new SurveyReading { Ssid = "office", Bssid = "AA-BB-CC-11-22-33", Rssi = -52, Channel = 36, Band = 5f },
                    new SurveyReading { Ssid = "guest", Bssid = "AA-BB-CC-44-55-66", Rssi = -71, Channel = 11, Band = 2.4f },
                ],
            },
        ],
    };

    [Fact]
    public void A_survey_round_trips_through_json()
    {
        string path = TempPath();
        try
        {
            SurveyStore.Save(path, Sample());
            SurveyDocument? loaded = SurveyStore.Load(path, out string? error);

            Assert.Null(error);
            Assert.NotNull(loaded);
            Assert.Equal("3F 事務所", loaded!.Name);
            Assert.Equal("20260815-1030-floor.png", loaded.FloorImageFile);
            Assert.Equal(1.6, loaded.AspectRatio);
            SurveyPoint point = Assert.Single(loaded.Points);
            Assert.Equal(0.25, point.X);
            Assert.Equal("AA-BB-CC-11-22-33", point.ConnectedBssid);
            Assert.Equal(2, point.Readings.Count);
            Assert.Equal(-52, point.Readings[0].Rssi);
            Assert.Equal(5f, point.Readings[0].Band);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_is_not_an_error()
    {
        SurveyDocument? loaded = SurveyStore.Load(TempPath(), out string? error);

        Assert.Null(loaded);
        Assert.Null(error);
    }

    [Fact]
    public void Broken_json_reports_an_error_instead_of_throwing()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not json");
            SurveyDocument? loaded = SurveyStore.Load(path, out string? error);

            Assert.Null(loaded);
            Assert.NotNull(error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_grid_survey_without_an_image_round_trips()
    {
        string path = TempPath();
        try
        {
            var document = Sample();
            document.FloorImageFile = null;

            SurveyStore.Save(path, document);
            SurveyDocument? loaded = SurveyStore.Load(path, out _);

            Assert.NotNull(loaded);
            Assert.Null(loaded!.FloorImageFile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Defaults_cover_version_and_aspect()
    {
        var document = new SurveyDocument();

        Assert.Equal(1, document.Version);
        Assert.Equal(4.0 / 3.0, document.AspectRatio);
        Assert.Empty(document.Points);
    }
}
