using System.Globalization;

namespace NowPlaying.Core;

/// <summary>A copy of the timeline numbers a media session reports (position is as of <see cref="LastUpdated"/>).</summary>
public readonly record struct TimelineState(TimeSpan Position, TimeSpan Start, TimeSpan End, DateTimeOffset LastUpdated, bool CanSeek);

/// <summary>Pure rules for the position/seek logic of the details page.</summary>
public static class PlaybackTimeline
{
    public const int SegmentCount = 20;

    public static readonly TimeSpan SkipStep = TimeSpan.FromSeconds(10);

    /// <summary>True when the session reports a usable duration.</summary>
    public static bool HasTimeline(TimelineState t) => t.End > t.Start;

    /// <summary>
    /// The position right now. While playing, the last reported position is extrapolated by the time that has
    /// passed since it was reported; the result always stays within [Start, End].
    /// </summary>
    public static TimeSpan CurrentPosition(TimelineState t, bool playing, DateTimeOffset now)
    {
        var position = t.Position;
        if (playing && t.LastUpdated.Year > 2000)
        {
            var passed = now - t.LastUpdated;
            if (passed > TimeSpan.Zero)
            {
                position += passed;
            }
        }

        return Clamp(position, t.Start, t.End);
    }

    /// <summary>Target of a click on the bar: Start + fraction * (End - Start). The fraction is clamped to [0, 1].</summary>
    public static TimeSpan SeekTarget(double fraction, TimeSpan start, TimeSpan end)
    {
        if (double.IsNaN(fraction))
        {
            fraction = 0;
        }

        fraction = Math.Clamp(fraction, 0, 1);
        var span = end - start;
        return span <= TimeSpan.Zero ? start : start + TimeSpan.FromTicks((long)(span.Ticks * fraction));
    }

    /// <summary>Position moved by <paramref name="delta"/>, clamped to [Start, End].</summary>
    public static TimeSpan Offset(TimeSpan position, TimeSpan delta, TimeSpan start, TimeSpan end) =>
        Clamp(position + delta, start, end);

    /// <summary>How many of <paramref name="segments"/> bar segments are filled for the position.</summary>
    public static int FilledSegments(TimeSpan position, TimeSpan start, TimeSpan end, int segments = SegmentCount)
    {
        var span = end - start;
        if (span <= TimeSpan.Zero || segments <= 0)
        {
            return 0;
        }

        var fraction = Math.Clamp((double)(position - start).Ticks / span.Ticks, 0, 1);
        return Math.Clamp((int)Math.Round(fraction * segments, MidpointRounding.AwayFromZero), 0, segments);
    }

    /// <summary>Fraction (0..1) a click on segment <paramref name="index"/> seeks to: the middle of the segment.</summary>
    public static double SegmentFraction(int index, int segments = SegmentCount) => (index + 0.5) / segments;

    /// <summary>"3:05"; "1:03:05" when it is an hour or more (or when <paramref name="forceHours"/> is set).</summary>
    public static string FormatTime(TimeSpan time, bool forceHours = false)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var totalHours = (long)time.TotalHours;
        return forceHours || totalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{totalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }

    /// <summary>"1:23 / 3:45" (elapsed / duration) relative to the start of the timeline.</summary>
    public static string FormatProgress(TimeSpan position, TimeSpan start, TimeSpan end)
    {
        var duration = end - start;
        var forceHours = duration.TotalHours >= 1;
        return $"{FormatTime(position - start, forceHours)} / {FormatTime(duration, forceHours)}";
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (max < min)
        {
            return min;
        }

        return value < min ? min : value > max ? max : value;
    }
}
