using System.Buffers;
using System.Text;
using System.Text.Json;

namespace NowPlaying.Core;

/// <summary>What the details card should show.</summary>
public readonly record struct SeekCardState(
    bool HasSession,
    bool Playing,
    bool ShowBar,
    int Filled,
    string? TimeText,
    string? Title = null,
    string? Artist = null,
    string? Detail = null,
    string? ArtUri = null);

/// <summary>A click/button on the details card, parsed from the submitted JSON.</summary>
public readonly record struct SubmitPayload(string Action, double? Fraction)
{
    public const string Seek = "seek";
    public const string Previous = "prev";
    public const string Next = "next";
    public const string PlayPause = "playpause";
    public const string Back = "back10";
    public const string Forward = "fwd10";

    /// <summary>Reads {"action": "...", "fraction": 0.5} from the action data, falling back to the form inputs.</summary>
    public static SubmitPayload Parse(string? data, string? inputs)
    {
        var fromData = TryParse(data);
        return fromData.Action.Length > 0 ? fromData : TryParse(inputs);
    }

    private static SubmitPayload TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SubmitPayload(string.Empty, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new SubmitPayload(string.Empty, null);
            }

            var action = string.Empty;
            double? fraction = null;
            if (doc.RootElement.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
            {
                action = a.GetString() ?? string.Empty;
            }

            if (doc.RootElement.TryGetProperty("fraction", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetDouble(out var d))
            {
                fraction = d;
            }

            return new SubmitPayload(action, fraction);
        }
        catch (JsonException)
        {
            return new SubmitPayload(string.Empty, null);
        }
    }
}

/// <summary>
/// Builds the Adaptive Card (1.5) for the details page: a seek bar made of 20 clickable columns, the time text
/// and the transport buttons. Written with <see cref="Utf8JsonWriter"/> so it is trim/AOT safe.
/// </summary>
public static class SeekCard
{
    public static string Build(SeekCardState state)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "AdaptiveCard");
            w.WriteString("$schema", "http://adaptivecards.io/schemas/adaptive-card.json");
            w.WriteString("version", "1.5");
            w.WriteStartArray("body");

            if (!state.HasSession)
            {
                WriteText(w, "Nothing is playing", "Large", "Bolder", subtle: false);
                WriteText(w, "Start playing something in Spotify, YouTube or another app.", null, null, subtle: true);
            }
            else
            {
                if (!string.IsNullOrEmpty(state.Title))
                {
                    WriteHeader(w, state);
                }

                if (state.ShowBar)
                {
                    WriteBar(w, state.Filled);
                }

                if (!string.IsNullOrEmpty(state.TimeText))
                {
                    w.WriteStartObject();
                    w.WriteString("type", "TextBlock");
                    w.WriteString("text", state.TimeText);
                    w.WriteString("horizontalAlignment", "Center");
                    w.WriteBoolean("isSubtle", true);
                    w.WriteEndObject();
                }

                WriteButtons(w, state);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Album art on the left, title / artist / album · app on the right.
    private static void WriteHeader(Utf8JsonWriter w, SeekCardState state)
    {
        w.WriteStartObject();
        w.WriteString("type", "ColumnSet");
        w.WriteStartArray("columns");

        if (!string.IsNullOrEmpty(state.ArtUri))
        {
            w.WriteStartObject();
            w.WriteString("type", "Column");
            w.WriteString("width", "auto");
            w.WriteStartArray("items");
            w.WriteStartObject();
            w.WriteString("type", "Image");
            w.WriteString("url", state.ArtUri);
            w.WriteString("altText", "Album art");
            w.WriteString("width", "120px");
            w.WriteString("height", "120px");
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteStartObject();
        w.WriteString("type", "Column");
        w.WriteString("width", "stretch");
        w.WriteString("verticalContentAlignment", "Center");
        w.WriteStartArray("items");
        WriteText(w, state.Title, "Large", "Bolder", subtle: false);
        WriteText(w, state.Artist, "Medium", null, subtle: false);
        WriteText(w, state.Detail, null, null, subtle: true);
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteText(Utf8JsonWriter w, string? text, string? size, string? weight, bool subtle)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        w.WriteStartObject();
        w.WriteString("type", "TextBlock");
        w.WriteString("text", text);
        w.WriteBoolean("wrap", true);
        w.WriteNumber("maxLines", 2);
        if (size is not null)
        {
            w.WriteString("size", size);
        }

        if (weight is not null)
        {
            w.WriteString("weight", weight);
        }

        w.WriteBoolean("isSubtle", subtle);
        w.WriteString("spacing", "Small");
        w.WriteEndObject();
    }

    private static void WriteBar(Utf8JsonWriter w, int filled)
    {
        w.WriteStartObject();
        w.WriteString("type", "ColumnSet");
        w.WriteString("spacing", "Medium");
        w.WriteStartArray("columns");
        for (var i = 0; i < PlaybackTimeline.SegmentCount; i++)
        {
            w.WriteStartObject();
            w.WriteString("type", "Column");
            w.WriteString("width", "1");
            w.WriteString("style", i < filled ? "accent" : "emphasis");
            w.WriteString("minHeight", "8px");
            w.WriteBoolean("bleed", false);
            w.WriteString("spacing", "None");

            // A non-breaking space keeps the column from collapsing in renderers that skip empty containers.
            w.WriteStartArray("items");
            w.WriteStartObject();
            w.WriteString("type", "TextBlock");
            w.WriteString("text", " ");
            w.WriteString("size", "Small");
            w.WriteString("spacing", "None");
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartObject("selectAction");
            w.WriteString("type", "Action.Submit");
            w.WriteStartObject("data");
            w.WriteString("action", SubmitPayload.Seek);
            w.WriteNumber("fraction", Math.Round(PlaybackTimeline.SegmentFraction(i), 4));
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteButtons(Utf8JsonWriter w, SeekCardState state)
    {
        w.WriteStartObject();
        w.WriteString("type", "ActionSet");
        w.WriteStartArray("actions");
        WriteButton(w, "⏮", SubmitPayload.Previous);
        if (state.ShowBar)
        {
            WriteButton(w, "−10s", SubmitPayload.Back);
        }

        WriteButton(w, state.Playing ? "Pause" : "Play", SubmitPayload.PlayPause);
        if (state.ShowBar)
        {
            WriteButton(w, "+10s", SubmitPayload.Forward);
        }

        WriteButton(w, "⏭", SubmitPayload.Next);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteButton(Utf8JsonWriter w, string title, string action)
    {
        w.WriteStartObject();
        w.WriteString("type", "Action.Submit");
        w.WriteString("title", title);
        w.WriteStartObject("data");
        w.WriteString("action", action);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
