using Xunit;
using System.Text.Json;
using NowPlaying.Core;

namespace NowPlaying.Tests;

public class PlaybackTimelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TimelineState Line(double pos, double end, double agoSeconds = 0, double start = 0) =>
        new(TimeSpan.FromSeconds(pos), TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), Now - TimeSpan.FromSeconds(agoSeconds), true);

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(83, "1:23")]
    [InlineData(225, "3:45")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3785, "1:03:05")]
    [InlineData(-4, "0:00")]
    public void FormatTime_FormatsMinutesAndHours(double seconds, string expected) =>
        Assert.Equal(expected, PlaybackTimeline.FormatTime(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void FormatProgress_UsesElapsedOverDuration() =>
        Assert.Equal("1:23 / 3:45", PlaybackTimeline.FormatProgress(TimeSpan.FromSeconds(83), TimeSpan.Zero, TimeSpan.FromSeconds(225)));

    [Fact]
    public void FormatProgress_LongTrackShowsHoursOnBoth() =>
        Assert.Equal("0:01:23 / 1:05:00", PlaybackTimeline.FormatProgress(TimeSpan.FromSeconds(83), TimeSpan.Zero, TimeSpan.FromMinutes(65)));

    [Fact]
    public void CurrentPosition_PlayingExtrapolates() =>
        Assert.Equal(TimeSpan.FromSeconds(40), PlaybackTimeline.CurrentPosition(Line(30, 200, agoSeconds: 10), true, Now));

    [Fact]
    public void CurrentPosition_PausedDoesNotMove() =>
        Assert.Equal(TimeSpan.FromSeconds(30), PlaybackTimeline.CurrentPosition(Line(30, 200, agoSeconds: 10), false, Now));

    [Fact]
    public void CurrentPosition_ClampsToEnd() =>
        Assert.Equal(TimeSpan.FromSeconds(200), PlaybackTimeline.CurrentPosition(Line(190, 200, agoSeconds: 60), true, Now));

    [Fact]
    public void CurrentPosition_IgnoresMissingUpdateTime()
    {
        var t = new TimelineState(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromSeconds(200), default, true);
        Assert.Equal(TimeSpan.FromSeconds(30), PlaybackTimeline.CurrentPosition(t, true, Now));
    }

    [Fact]
    public void CurrentPosition_FutureUpdateTimeDoesNotGoBack() =>
        Assert.Equal(TimeSpan.FromSeconds(30), PlaybackTimeline.CurrentPosition(Line(30, 200, agoSeconds: -5), true, Now));

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 100)]
    [InlineData(1.0, 200)]
    [InlineData(-3.0, 0)]
    [InlineData(7.0, 200)]
    [InlineData(double.NaN, 0)]
    public void SeekTarget_ClampsFraction(double fraction, double expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PlaybackTimeline.SeekTarget(fraction, TimeSpan.Zero, TimeSpan.FromSeconds(200)));

    [Fact]
    public void SeekTarget_HonoursStartOffset() =>
        Assert.Equal(TimeSpan.FromSeconds(150), PlaybackTimeline.SeekTarget(0.5, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(200)));

    [Theory]
    [InlineData(50, 10, 60)]
    [InlineData(50, -10, 40)]
    [InlineData(5, -10, 0)]
    [InlineData(195, 10, 200)]
    public void Offset_ClampsToRange(double position, double delta, double expected) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expected),
            PlaybackTimeline.Offset(TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(delta), TimeSpan.Zero, TimeSpan.FromSeconds(200)));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 10)]
    [InlineData(200, 20)]
    [InlineData(4, 0)]
    [InlineData(6, 1)]
    [InlineData(500, 20)]
    public void FilledSegments_FollowsFraction(double position, int expected) =>
        Assert.Equal(expected, PlaybackTimeline.FilledSegments(TimeSpan.FromSeconds(position), TimeSpan.Zero, TimeSpan.FromSeconds(200)));

    [Fact]
    public void FilledSegments_NoTimelineIsZero() =>
        Assert.Equal(0, PlaybackTimeline.FilledSegments(TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.Zero));

    [Fact]
    public void HasTimeline_RequiresPositiveDuration()
    {
        Assert.True(PlaybackTimeline.HasTimeline(Line(0, 10)));
        Assert.False(PlaybackTimeline.HasTimeline(Line(0, 0)));
    }

    [Fact]
    public void SeekCard_WithBar_IsValidJsonWith20Segments()
    {
        var json = SeekCard.Build(new SeekCardState(true, true, true, 7, "1:23 / 3:45"));
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement.GetProperty("body");
        Assert.Equal("1.5", doc.RootElement.GetProperty("version").GetString());

        var columns = body[0].GetProperty("columns");
        Assert.Equal(20, columns.GetArrayLength());
        Assert.Equal(7, columns.EnumerateArray().Count(c => c.GetProperty("style").GetString() == "accent"));
        Assert.All(columns.EnumerateArray(), c =>
        {
            Assert.Equal("Action.Submit", c.GetProperty("selectAction").GetProperty("type").GetString());
            Assert.Equal("seek", c.GetProperty("selectAction").GetProperty("data").GetProperty("action").GetString());
        });
        Assert.Equal(0.025, columns[0].GetProperty("selectAction").GetProperty("data").GetProperty("fraction").GetDouble(), 4);
        Assert.Equal("1:23 / 3:45", body[1].GetProperty("text").GetString());

        var actions = body[2].GetProperty("actions");
        Assert.Equal(5, actions.GetArrayLength());
        Assert.Equal("Pause", actions[2].GetProperty("title").GetString());
    }

    [Fact]
    public void SeekCard_WithoutTimeline_HidesBarAndSeekButtons()
    {
        var json = SeekCard.Build(new SeekCardState(true, false, false, 0, null));
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement.GetProperty("body");
        Assert.Equal(1, body.GetArrayLength());
        Assert.DoesNotContain("ColumnSet", json, StringComparison.Ordinal);
        var actions = body[0].GetProperty("actions");
        Assert.Equal(3, actions.GetArrayLength());
        Assert.Equal("Play", actions[1].GetProperty("title").GetString());
    }

    [Fact]
    public void SeekCard_NoSession_ShowsOnlyMessage()
    {
        using var doc = JsonDocument.Parse(SeekCard.Build(default));
        var body = doc.RootElement.GetProperty("body");
        Assert.Equal(2, body.GetArrayLength());
        Assert.All(body.EnumerateArray(), e => Assert.Equal("TextBlock", e.GetProperty("type").GetString()));
        Assert.Equal("Nothing is playing", body[0].GetProperty("text").GetString());
    }

    [Fact]
    public void SeekCard_Header_HasArtAndTitle()
    {
        var json = SeekCard.Build(new SeekCardState(true, true, true, 5, "1:00 / 3:00", "Song", "Artist", "Album · Spotify", "file:///C:/art/x-200.png"));
        using var doc = JsonDocument.Parse(json);
        var header = doc.RootElement.GetProperty("body")[0];
        Assert.Equal("ColumnSet", header.GetProperty("type").GetString());
        var image = header.GetProperty("columns")[0].GetProperty("items")[0];
        Assert.Equal("Image", image.GetProperty("type").GetString());
        Assert.Equal("file:///C:/art/x-200.png", image.GetProperty("url").GetString());
        Assert.Equal("Song", header.GetProperty("columns")[1].GetProperty("items")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void SubmitPayload_ParsesSeekAndButtons()
    {
        var seek = SubmitPayload.Parse("{\"action\":\"seek\",\"fraction\":0.375}", null);
        Assert.Equal("seek", seek.Action);
        Assert.Equal(0.375, seek.Fraction);
        Assert.Equal("prev", SubmitPayload.Parse("{\"action\":\"prev\"}", null).Action);
    }

    [Fact]
    public void SubmitPayload_FallsBackToInputsAndSurvivesGarbage()
    {
        Assert.Equal("next", SubmitPayload.Parse("", "{\"action\":\"next\"}").Action);
        Assert.Equal(string.Empty, SubmitPayload.Parse("not json", null).Action);
        Assert.Equal(string.Empty, SubmitPayload.Parse("[1,2]", null).Action);
        Assert.Equal(string.Empty, SubmitPayload.Parse(null, null).Action);
    }
}
