// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Text.Json;

namespace Mimic.Core;

public sealed record Segment(long StartMs, long EndMs, string Text);

public sealed record Video
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public string? LocalVideoPath { get; init; }
    public IReadOnlyList<string> MediaHosts { get; init; } = [];
    public required string Title { get; init; }
    public required string Channel { get; init; }
    public long DurationSeconds { get; init; }
    public long TranscodeSeconds { get; init; }
    public required string Language { get; init; }
    /// <summary>"captions", "auto-captions", "whisper", "capture" or "image"</summary>
    public required string Method { get; init; }
    public long CreatedAt { get; init; }
}

public sealed record Transcript(Video Video, IReadOnlyList<Segment> Segments, string? Notice);

public sealed record Passage(long StartMs, string Clock, string Link, string Title, string Text);

public sealed record TranscriptView
{
    public required Video Video { get; init; }
    public required IReadOnlyList<Passage> Passages { get; init; }
    public required IReadOnlyList<Segment> Segments { get; init; }
    public int Words { get; init; }
    public string? Takeaways { get; init; }
    /// <summary>Pictures only.</summary>
    public ImageScore? Score { get; init; }
    /// <summary>Set when the transcript is fine but something after it failed.</summary>
    public string? Notice { get; init; }
}

public sealed record PlaybackSource(string Url, bool Local);

/// <summary>What sits under the crosshair. Frames run from the innermost out to the page itself.</summary>
public sealed record Target
{
    public IReadOnlyList<string> Frames { get; init; } = [];
    public string Address { get; init; } = "";
    public required string Title { get; init; }
    public uint ProcessId { get; init; }

    /// <summary>Every address worth trying, the clicked frame first.</summary>
    internal List<string> Links()
    {
        var links = new List<string>();
        foreach (var link in Frames.Append(Address))
        {
            if (link.Length > 0 && !links.Contains(link)) links.Add(link);
        }
        return links;
    }

    /// <summary>The page holding the video, which sites check before serving an embed.</summary>
    internal string? Page() => Links().LastOrDefault();

    internal static Target FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("expected an object");
        var frames = new List<string>();
        if (root.TryGetProperty("frames", out var list))
        {
            foreach (var frame in list.EnumerateArray()) frames.Add(Text(frame));
        }
        return new Target
        {
            Frames = frames,
            Address = root.TryGetProperty("address", out var address) ? Text(address) : "",
            Title = Text(Required(root, "title")),
            ProcessId = Required(root, "process_id").GetUInt32(),
        };
    }

    private static JsonElement Required(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value : throw new JsonException($"missing field `{name}`");

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("expected a string");
}

/// <summary>How certain the model is that a picture is AI-generated, and why.</summary>
public sealed record ImageScore(long Score, string Identification, IReadOnlyList<string> Reasons)
{
    /// <summary>What the score says, in six tiers.</summary>
    public string Certainty => Score switch
    {
        >= 90 => "Almost certainly AI-generated",
        >= 70 => "Probably AI-generated",
        >= 50 => "Leaning AI-generated",
        >= 30 => "Leaning not AI-generated",
        >= 10 => "Probably not AI-generated",
        _ => "Almost certainly not AI-generated",
    };

    internal string ToJson() =>
        $"{{\"score\":{Score},\"identification\":{Sys.JsonString(Identification)},\"reasons\":{Sys.JsonArray(Reasons)}}}";

    internal static ImageScore FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("expected an object");
        var reasons = new List<string>();
        foreach (var reason in Field(root, "reasons").EnumerateArray())
        {
            reasons.Add(reason.ValueKind == JsonValueKind.String ? reason.GetString()! : throw new JsonException("expected a string"));
        }
        var identification = Field(root, "identification");
        if (identification.ValueKind != JsonValueKind.String) throw new JsonException("expected a string");
        return new ImageScore(Field(root, "score").GetInt64(), identification.GetString()!, reasons);
    }

    private static JsonElement Field(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value : throw new JsonException($"missing field `{name}`");
}

/// <summary>
/// What the user picked with the Model button. <see cref="Kind"/> is "default" (Haiku for titles and
/// Sonnet for images), "claude", "openai" (the installed Codex command, on the ChatGPT account it is
/// signed in to) or "server" (anything that speaks the OpenAI chat API, such as Ollama or LM Studio).
/// </summary>
public sealed record ModelChoice
{
    public const string DefaultKind = "default";
    public const string ClaudeKind = "claude";
    public const string OpenAIKind = "openai";
    public const string ServerKind = "server";

    public string Kind { get; init; } = DefaultKind;
    public string Model { get; init; } = "";
    public string Address { get; init; } = "";
    public string Key { get; init; } = "";

    public static ModelChoice Default { get; } = new();
    public static ModelChoice Claude(string model) => new() { Kind = ClaudeKind, Model = model };
    public static ModelChoice OpenAI(string model) => new() { Kind = OpenAIKind, Model = model };
    public static ModelChoice Server(string address, string model, string key) =>
        new() { Kind = ServerKind, Address = address, Model = model, Key = key };

    private const string UnknownKind = "unknown variant `{0}`, expected one of `default`, `claude`, `openai`, `server`";

    /// <summary>Throws <see cref="MimicException"/> when the choice cannot be used.</summary>
    public void Check()
    {
        switch (Kind)
        {
            case DefaultKind:
                return;
            case ClaudeKind:
                if (Model.Trim().Length == 0) throw new MimicException("Pick a Claude model.");
                return;
            case OpenAIKind:
                if (Model.Trim().Length == 0) throw new MimicException("Pick or type an OpenAI model.");
                return;
            case ServerKind:
                if (!Address.StartsWith("http://", StringComparison.Ordinal) && !Address.StartsWith("https://", StringComparison.Ordinal))
                {
                    throw new MimicException("The server address must start with http:// or https://.");
                }
                if (Model.Trim().Length == 0) throw new MimicException("Enter the model's name on that server.");
                return;
            default:
                throw new MimicException(string.Format(UnknownKind, Kind));
        }
    }

    /// <summary>The JSON the library has always stored: {"kind":"claude","model":"…"} and so on.</summary>
    public string ToJson() => Kind switch
    {
        DefaultKind => "{\"kind\":\"default\"}",
        ClaudeKind or OpenAIKind => $"{{\"kind\":\"{Kind}\",\"model\":{Sys.JsonString(Model)}}}",
        ServerKind => $"{{\"kind\":\"server\",\"address\":{Sys.JsonString(Address)},\"model\":{Sys.JsonString(Model)},\"key\":{Sys.JsonString(Key)}}}",
        _ => throw new MimicException(string.Format(UnknownKind, Kind)),
    };

    public static ModelChoice FromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("expected an object");
            var kind = Field(root, "kind");
            return kind switch
            {
                DefaultKind => Default,
                ClaudeKind => Claude(Field(root, "model")),
                OpenAIKind => OpenAI(Field(root, "model")),
                ServerKind => Server(Field(root, "address"), Field(root, "model"), Field(root, "key")),
                _ => throw new JsonException(string.Format(UnknownKind, kind)),
            };
        }
        catch (JsonException error)
        {
            throw new MimicException(error.Message);
        }
    }

    private static string Field(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) throw new JsonException($"missing field `{name}`");
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("expected a string");
    }
}
