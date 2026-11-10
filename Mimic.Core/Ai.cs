// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mimic.Core;

/// <summary>
/// Section titles and image reverse prompts, through the installed Claude Code command,
/// the installed Codex command, or a server the user plugged in.
/// </summary>
public static class Ai
{
    internal const string TitleModel = "claude-haiku-5-5";
    internal const string ImageModel = "claude-sonnet-5-5";
    internal const int TitleBatchSize = 28;
    internal const int SummaryBatchChars = 12_000;

    /// <summary>How the prompts tell Claude where the image is. A server is sent the image instead.</summary>
    internal const string ReadImage = "Read the file NAME in the current directory. It is an image.";
    internal const string ImageAttached = "The attached picture is the image.";

    internal const string ImagePrompt = Prompts.Image;
    internal const string ScorePrompt = Prompts.Score;

    /// <summary>Text in a file that names the tool that made it.</summary>
    private static readonly string[] GeneratorMarks =
    [
        "c2pa", "trainedAlgorithmicMedia", "compositeWithTrainedAlgorithmicMedia", "Midjourney", "DALL-E", "DALL·E", "OpenAI",
        "ChatGPT", "Stable Diffusion", "ComfyUI", "Automatic1111", "parameters", "Negative prompt", "NovelAI", "Firefly",
        "Imagen", "Gemini", "SynthID", "FLUX", "Grok", "Ideogram", "Leonardo",
    ];
    private const int ClueChars = 200;
    private const int ExifSpan = 4096;
    private const int MinExifText = 4;

    internal static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static bool Readable(byte b) => b is >= 0x21 and <= 0x7E || b == (byte)' ';

    /// <summary>The readable text following a position in a file.</summary>
    private static string TextAfter(ReadOnlySpan<byte> bytes, int start)
    {
        var text = new StringBuilder();
        for (var i = start; i < bytes.Length && i - start < ClueChars && Readable(bytes[i]); i++) text.Append((char)bytes[i]);
        return text.ToString();
    }

    /// <summary>Generator names, and camera details from EXIF, found in an image file's bytes.</summary>
    internal static List<string> FileClues(ReadOnlySpan<byte> bytes)
    {
        var clues = new List<string>();
        foreach (var mark in GeneratorMarks)
        {
            var at = bytes.IndexOf(Encoding.UTF8.GetBytes(mark));
            if (at >= 0) clues.Add(TextAfter(bytes, at));
        }
        var exif = bytes.IndexOf("Exif\0\0"u8);
        if (exif >= 0)
        {
            var span = bytes[(exif + 6)..Math.Min(bytes.Length, exif + 6 + ExifSpan)];
            var text = new List<string>();
            var run = new StringBuilder();
            foreach (var b in span)
            {
                if (Readable(b))
                {
                    run.Append((char)b);
                    continue;
                }
                if (run.Length >= MinExifText) text.Add(run.ToString());
                run.Clear();
            }
            if (run.Length >= MinExifText) text.Add(run.ToString());
            clues.Add($"EXIF text: {string.Join(" | ", text)}");
        }
        return clues;
    }

    /// <summary>
    /// A Claude run without the user's hooks, plugins and rules, which would slow it down
    /// and have nothing to do with this job.
    /// </summary>
    private static ToolCommand Claude(Paths paths, string model, string tools, string dir)
    {
        var command = new ToolCommand(paths.Claude()) { WorkingDirectory = dir };
        return command
            .Args("-p", "--model", model, "--setting-sources", "", "--strict-mcp-config")
            .Args("--no-session-persistence", "--tools", tools);
    }

    private static string WorkFolder(Paths paths)
    {
        var dir = Sys.Join(paths.WorkDir(), $"ai-{Sys.UniqueName()}");
        Sys.Io(() => Directory.CreateDirectory(dir), e => $"Could not create the Codex work folder '{dir}': {e}");
        return dir;
    }

    /// <summary>A saved picture as a data URL.</summary>
    public static string DataUrl(string path)
    {
        var bytes = Sys.Io(() => File.ReadAllBytes(path));
        var kind = Sys.Extension(path) ?? "";
        if (kind is "jpg" or "jpeg") kind = "jpeg";
        return $"data:image/{kind};base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>What a curl config line needs so a value stays one quoted string.</summary>
    private static string CurlQuoted(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    internal static JsonNode ServerRequest(string model, string prompt, string? picture)
    {
        JsonNode content = picture is null
            ? JsonValue.Create(prompt)
            : new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = prompt.Replace(ReadImage, ImageAttached) },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = picture } });
        return new JsonObject
        {
            ["model"] = model.Trim(),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };
    }

    internal static string ServerReply(string reply)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(reply);
        }
        catch (JsonException error)
        {
            throw new MimicException($"Could not read the server's reply: {error.Message}");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString()!;
            }
            throw new MimicException("The server's reply had no text in it.");
        }
    }

    /// <summary>
    /// Goes through curl, so Cancel can stop it like any other tool. The key travels in a
    /// config fed to stdin from memory, which keeps it off the command line and off the disk.
    /// </summary>
    internal static string AskServer(Job job, Paths paths, ModelChoice server, string prompt, string? image)
    {
        var picture = image is null ? null : DataUrl(image);
        var dir = WorkFolder(paths);
        string result;
        try
        {
            var body = ServerRequest(server.Model, prompt, picture).ToJsonString(Json);
            Sys.Io(() => File.WriteAllText(Sys.Join(dir, "body.json"), body, Sys.Utf8));
            var config = $"url = {CurlQuoted($"{server.Address.Trim().TrimEnd('/')}/chat/completions")}\nheader = \"Content-Type: application/json\"\n";
            if (server.Key.Trim().Length > 0) config += $"header = {CurlQuoted($"Authorization: Bearer {server.Key.Trim()}")}\n";
            var command = new ToolCommand("curl") { WorkingDirectory = dir }
                .Args("-sS", "--fail-with-body", "-K", "-", "--data-binary", "@body.json");
            result = Tools.RunWithText(job, command, "The model server", config);
        }
        finally
        {
            Sys.RemoveDirAll(dir);
        }
        return ServerReply(result);
    }

    /// <summary>
    /// A Codex run on the signed-in account, without the user's config, hooks and plugins, and
    /// without a shell, because a transcript or picture could carry instructions.
    /// </summary>
    private static string AskOpenAI(Job job, Paths paths, string model, string prompt, string? image)
    {
        var dir = WorkFolder(paths);
        try
        {
            var input = Sys.Join(dir, "prompt.txt");
            Sys.Io(() => File.WriteAllText(input, prompt.Replace(ReadImage, ImageAttached), Sys.Utf8));
            var command = new ToolCommand(paths.Codex()) { WorkingDirectory = dir }
                .Args("exec", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules")
                .Args("--color", "never", "-s", "read-only", "-m", model.Trim(), "-o", "reply.txt")
                .Args("--disable", "shell_tool", "--disable", "hooks", "--disable", "memories")
                .Args("--disable", "plugins", "--disable", "multi_agent", "--disable", "skill_search")
                .Args("-c", "project_doc_max_bytes=0", "-c", "web_search=\"disabled\"");
            if (image is not null) command.Args("-i", image);
            Tools.RunWithInput(job, command, "Codex", input);
            return Sys.Io(() => File.ReadAllText(Sys.Join(dir, "reply.txt"), Sys.Utf8));
        }
        finally
        {
            Sys.RemoveDirAll(dir);
        }
    }

    /// <summary>Sends a text-only request through the model the user selected.</summary>
    private static string AskText(Job job, Paths paths, ModelChoice choice, string defaultModel, string prompt) => choice.Kind switch
    {
        ModelChoice.ServerKind => AskServer(job, paths, choice, prompt, null),
        ModelChoice.OpenAIKind => AskOpenAI(job, paths, choice.Model, prompt, null),
        ModelChoice.ClaudeKind => AskClaudeText(job, paths, choice.Model, prompt),
        _ => AskClaudeText(job, paths, defaultModel, prompt),
    };

    private static string AskClaudeText(Job job, Paths paths, string model, string prompt)
    {
        var dir = WorkFolder(paths);
        try
        {
            var input = Sys.Join(dir, "prompt.txt");
            Sys.Io(() => File.WriteAllText(input, prompt, Sys.Utf8));
            return Tools.RunWithInput(job, Claude(paths, model, "", dir), "Claude", input);
        }
        finally
        {
            Sys.RemoveDirAll(dir);
        }
    }

    /// <summary>How Codex says it is signed in, or an error if it is not.</summary>
    public static string OpenAIStatus(Paths paths)
    {
        Tools.Run(new Job(), new ToolCommand(paths.Codex()).Args("login", "status"), "Codex");
        return "Signed in, no API key needed.";
    }

    /// <summary>Opens the browser for the ChatGPT sign-in and waits for it to finish.</summary>
    public static string OpenAISignIn(Paths paths)
    {
        Tools.Run(new Job(), new ToolCommand(paths.Codex()).Args("login"), "Codex");
        return OpenAIStatus(paths);
    }

    /// <summary>
    /// One short title for each passage, saying what that section explains.
    /// Creates a short title for every passage in bounded batches, without capping transcript length.
    /// </summary>
    public static List<string> SectionTitles(Job job, Paths paths, ModelChoice choice, IReadOnlyList<Segment> passages, Action<string> report)
    {
        var titles = new List<string>(passages.Count);
        for (var first = 0; first < passages.Count; first += TitleBatchSize)
        {
            if (job.Cancelled) throw new MimicException(Tools.Cancelled);
            var batch = passages.Skip(first).Take(TitleBatchSize).ToList();
            var start = first + 1;
            var end = start + batch.Count - 1;
            report($"Titling sections {start}–{end} of {passages.Count}…");
            var numbered = string.Concat(batch.Select((p, i) => $"{i + 1}: {p.Text}\n"));
            var prompt = $"{Prompts.TitleHead}{batch.Count}{Prompts.TitleTail}{numbered}";
            var reply = AskText(job, paths, choice, TitleModel, prompt);
            titles.AddRange(ParseTitles(reply, batch.Count));
        }
        return titles;
    }

    /// <summary>
    /// Analyzes every transcript passage in bounded requests, then reduces the complete set.
    /// The per-request size is not a cap on video duration or transcript length.
    /// </summary>
    public static string SummarizeTranscript(Job job, Paths paths, ModelChoice choice, IReadOnlyList<Segment> segments, Action<string> report)
    {
        if (segments.Count == 0) throw new MimicException("There is no transcript to summarize.");
        var excerpts = TranscriptExcerptBatches(segments);
        var candidates = new List<string>(excerpts.Count);
        for (var index = 0; index < excerpts.Count; index++)
        {
            if (job.Cancelled) throw new MimicException(Tools.Cancelled);
            report($"Analyzing transcript section {index + 1} of {excerpts.Count}…");
            candidates.Add(AskText(job, paths, choice, TitleModel, TakeawayPrompt(excerpts[index], false)));
        }
        while (candidates.Count > 1)
        {
            var reduced = new List<string>();
            var batches = candidates.Chunk(12).ToList();
            for (var index = 0; index < batches.Count; index++)
            {
                if (job.Cancelled) throw new MimicException(Tools.Cancelled);
                report($"Weighing key messages {index + 1}/{batches.Count}…");
                reduced.Add(AskText(job, paths, choice, TitleModel, TakeawayPrompt(string.Join("\n\n", batches[index]), true)));
            }
            candidates = reduced;
        }
        var result = (candidates.LastOrDefault() ?? "").Trim();
        if (result.Length == 0) throw new MimicException("The model returned no key takeaways.");
        return result;
    }

    internal static List<string> TranscriptExcerptBatches(IReadOnlyList<Segment> segments)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        var currentChars = 0;
        foreach (var segment in segments)
        {
            var timestamp = Captions.Clock(segment.StartMs);
            var line = new StringBuilder($"[{timestamp}] ");
            var lineChars = Sys.CharCount(line.ToString());
            foreach (var word in Sys.SplitWhitespace(segment.Text))
            {
                foreach (var ch in word.EnumerateRunes())
                {
                    if (lineChars >= SummaryBatchChars)
                    {
                        AppendExcerptLine(batches, current, ref currentChars, line.ToString());
                        line = new StringBuilder($"[{timestamp} continued] ");
                        lineChars = Sys.CharCount(line.ToString());
                    }
                    line.Append(ch.ToString());
                    lineChars++;
                }
                if (lineChars < SummaryBatchChars)
                {
                    line.Append(' ');
                    lineChars++;
                }
            }
            if (line.ToString().Trim().Length > 0) AppendExcerptLine(batches, current, ref currentChars, line.ToString());
        }
        if (current.Length > 0) batches.Add(current.ToString());
        return batches;
    }

    private static void AppendExcerptLine(List<string> batches, StringBuilder current, ref int currentChars, string line)
    {
        var lineChars = Sys.CharCount(line) + 1;
        if (currentChars + lineChars > SummaryBatchChars && current.Length > 0)
        {
            batches.Add(current.ToString());
            current.Clear();
            currentChars = 0;
        }
        current.Append(line.TrimEnd()).Append('\n');
        currentChars += lineChars;
    }

    internal static string TakeawayPrompt(string source, bool candidates) =>
        $"{(candidates ? Prompts.TakeawayCombine : Prompts.TakeawaySingle)}{Prompts.TakeawayTail}{source}";

    /// <summary>The text from the first { to the last }, or nothing when they are out of order.</summary>
    private static string Braced(string reply, string missing)
    {
        var start = reply.IndexOf('{');
        if (start < 0) throw new MimicException(missing);
        var end = reply.LastIndexOf('}');
        if (end < 0) throw new MimicException(missing);
        return end >= start ? reply[start..(end + 1)] : "";
    }

    /// <summary>A section the model skipped gets an empty title rather than failing the rest.</summary>
    internal static List<string> ParseTitles(string reply, int expected)
    {
        var json = Braced(reply, "The model did not return section titles.");
        var titles = new Dictionary<string, string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) throw new JsonException("expected a string");
                titles[entry.Name] = entry.Value.GetString()!;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new MimicException($"Could not read the model's section titles: {error.Message}");
        }
        return Enumerable.Range(1, expected).Select(n => titles.GetValueOrDefault(n.ToString(), "")).ToList();
    }

    /// <summary>Has the model look at the image and answer a prompt about it. For Claude, NAME is a copy of the file.</summary>
    private static string AskAboutImage(Job job, Paths paths, ModelChoice choice, string image, string prompt)
    {
        string claudeModel;
        switch (choice.Kind)
        {
            case ModelChoice.ServerKind:
                return AskServer(job, paths, choice, prompt, image);
            case ModelChoice.OpenAIKind:
                return AskOpenAI(job, paths, choice.Model, prompt, image);
            case ModelChoice.ClaudeKind:
                claudeModel = choice.Model;
                break;
            default:
                claudeModel = ImageModel;
                break;
        }
        var extension = Sys.Extension(image) ?? "png";
        var name = $"image.{extension}";
        var dir = WorkFolder(paths);
        try
        {
            Sys.Io(() => File.Copy(image, Sys.Join(dir, name), true));
            var input = Sys.Join(dir, "prompt.txt");
            Sys.Io(() => File.WriteAllText(input, prompt.Replace("NAME", name), Sys.Utf8));
            var command = Claude(paths, claudeModel, "Read", dir).Args("--allowedTools", "Read");
            return Tools.RunWithInput(job, command, "Claude", input);
        }
        finally
        {
            Sys.RemoveDirAll(dir);
        }
    }

    public static ImageScore AiScore(Job job, Paths paths, ModelChoice choice, string image)
    {
        var bytes = Sys.Io(() => File.ReadAllBytes(image));
        var found = FileClues(bytes);
        var clues = found.Count == 0 ? "None found." : string.Join("\n", found);
        return ParseScore(AskAboutImage(job, paths, choice, image, ScorePrompt.Replace("CLUES", clues)));
    }

    internal static ImageScore ParseScore(string reply)
    {
        var json = Braced(reply, "The model did not return a score.");
        try
        {
            var score = ImageScore.FromJson(json);
            return score with { Score = Math.Clamp(score.Score, 0, 100) };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            throw new MimicException($"Could not read the model's score: {error.Message}");
        }
    }

    public static string ReversePrompt(Job job, Paths paths, ModelChoice choice, string image)
    {
        var text = AskAboutImage(job, paths, choice, image, ImagePrompt).Trim();
        if (text.Length == 0) throw new MimicException("The model returned no description of the image.");
        return text;
    }
}
