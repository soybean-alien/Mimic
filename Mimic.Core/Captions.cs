// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mimic.Core;

/// <summary>Turns WebVTT captions and whisper.cpp JSON into timestamped segments.</summary>
public static class Captions
{
    /// <summary>
    /// Parses WebVTT. YouTube's automatic captions roll: each cue repeats the previous
    /// cue's line and adds one, so <paramref name="rolling"/> keeps only the lines that are new.
    /// </summary>
    public static List<Segment> ParseVtt(string vtt, bool rolling)
    {
        var normalized = vtt.Replace("\r\n", "\n").Replace('\r', '\n');
        var segments = new List<Segment>();
        var previousLines = new List<string>();

        foreach (var block in normalized.Split("\n\n"))
        {
            var lines = Sys.Lines(block);
            var timingAt = lines.FindIndex(line => line.Contains("-->"));
            if (timingAt < 0 || ParseTiming(lines[timingAt]) is not var (startMs, endMs)) continue;

            var cueLines = lines.Skip(timingAt + 1).Select(CleanCueText).Where(line => line.Length > 0).ToList();
            var newLines = rolling ? cueLines.Where(line => !previousLines.Contains(line)).ToList() : cueLines;

            if (newLines.Count > 0) segments.Add(new Segment(startMs, endMs, string.Join(" ", newLines)));
            if (cueLines.Count > 0) previousLines = cueLines;
        }
        return segments;
    }

    private static (long, long)? ParseTiming(string line)
    {
        var arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0) return null;
        var end = Sys.SplitWhitespace(line[(arrow + 3)..]).FirstOrDefault();
        if (end is null) return null;
        if (ParseTimestamp(line[..arrow].Trim()) is not { } startMs || ParseTimestamp(end) is not { } endMs) return null;
        return (startMs, endMs);
    }

    private static long? ParseTimestamp(string value)
    {
        var dot = value.IndexOf('.');
        if (dot < 0) return null;
        var parts = new List<long>();
        foreach (var part in value[..dot].Split(':'))
        {
            if (Whole(part) is not { } number) return null;
            parts.Add(number);
        }
        long seconds;
        if (parts.Count == 3) seconds = parts[0] * 3600 + parts[1] * 60 + parts[2];
        else if (parts.Count == 2) seconds = parts[0] * 60 + parts[1];
        else return null;
        return Whole(value[(dot + 1)..]) is { } millis ? seconds * 1000 + millis : null;
    }

    private static long? Whole(string text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string CleanCueText(string line)
    {
        var text = new StringBuilder(line.Length);
        var inTag = false;
        foreach (var ch in line)
        {
            if (ch == '<') inTag = true;
            else if (ch == '>' && inTag) inTag = false;
            else if (!inTag) text.Append(ch);
        }
        // Some captions escape their styling tags, so those only appear after decoding.
        var decoded = DecodeEntities(text.ToString());
        foreach (var tag in (string[])["<i>", "</i>", "<b>", "</b>", "<u>", "</u>"]) decoded = decoded.Replace(tag, "");
        return string.Join(" ", Sys.SplitWhitespace(decoded));
    }

    private static string DecodeEntities(string text) =>
        text.Replace("&nbsp;", " ")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&apos;", "'")
            .Replace("&lrm;", "")
            .Replace("&rlm;", "")
            .Replace("&amp;", "&");

    /// <summary>Parses the file written by <c>whisper-cli -oj</c>. Returns the segments and detected language.</summary>
    public static (List<Segment> Segments, string? Language) ParseWhisperJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("expected an object");
            string? language = null;
            if (root.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null)
            {
                if (result.ValueKind != JsonValueKind.Object) throw new JsonException("expected an object");
                if (result.TryGetProperty("language", out var heard) && heard.ValueKind != JsonValueKind.Null)
                {
                    language = heard.ValueKind == JsonValueKind.String ? heard.GetString() : throw new JsonException("expected a string");
                }
            }
            if (!root.TryGetProperty("transcription", out var items)) throw new JsonException("missing field `transcription`");
            var segments = new List<Segment>();
            foreach (var item in items.EnumerateArray())
            {
                var offsets = item.GetProperty("offsets");
                var from = offsets.GetProperty("from").GetInt64();
                var to = offsets.GetProperty("to").GetInt64();
                var text = item.GetProperty("text");
                if (text.ValueKind != JsonValueKind.String) throw new JsonException("expected a string");
                var trimmed = text.GetString()!.Trim();
                if (trimmed.Length > 0) segments.Add(new Segment(from, to, trimmed));
            }
            return (segments, language);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new MimicException($"Could not read Whisper's output: {error.Message}");
        }
    }

    /// <summary>Joins short caption lines into readable passages, each keeping its first timestamp.</summary>
    public static List<Segment> GroupIntoPassages(IReadOnlyList<Segment> segments, long maxMs)
    {
        var passages = new List<Segment>();
        foreach (var segment in segments)
        {
            if (passages.Count > 0 && segment.StartMs - passages[^1].StartMs < maxMs)
            {
                var last = passages[^1];
                passages[^1] = new Segment(last.StartMs, Math.Max(last.EndMs, segment.EndMs), last.Text + " " + segment.Text);
            }
            else
            {
                passages.Add(segment);
            }
        }
        return passages;
    }

    public static string ToSrt(IReadOnlyList<Segment> segments)
    {
        var srt = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            srt.Append($"{i + 1}\n{SrtTime(segment.StartMs)} --> {SrtTime(segment.EndMs)}\n{segment.Text}\n\n");
        }
        return srt.ToString();
    }

    private static string SrtTime(long ms)
    {
        ms = Math.Max(ms, 0);
        return $"{ms / 3_600_000:D2}:{ms / 60_000 % 60:D2}:{ms / 1000 % 60:D2},{ms % 1000:D3}";
    }

    public static string Clock(long ms)
    {
        var total = Math.Max(ms, 0) / 1000;
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:D2}:{total % 60:D2}"
            : $"{total / 60}:{total % 60:D2}";
    }
}
