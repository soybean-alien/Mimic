// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Text;

namespace Mimic.Core;

/// <summary>Turning a saved entry into a text file, subtitles, a vault note or a PDF.</summary>
public static class Exports
{
    internal const long PassageMs = 30_000;
    internal const string ImageMethod = "image";

    public static string PlainText(Transcript transcript, IReadOnlyDictionary<long, string> titles)
    {
        if (transcript.Video.Method == ImageMethod) return string.Join("\n\n", transcript.Segments.Select(s => s.Text));
        var text = new StringBuilder();
        foreach (var passage in Captions.GroupIntoPassages(transcript.Segments, PassageMs))
        {
            text.Append(titles.TryGetValue(passage.StartMs, out var title)
                ? $"[{Captions.Clock(passage.StartMs)}] {title}\n{passage.Text}\n\n"
                : $"[{Captions.Clock(passage.StartMs)}] {passage.Text}\n\n");
        }
        return text.ToString();
    }

    /// <summary>A note for a vault: frontmatter, then one heading per section saying what it covers.</summary>
    public static string ToMarkdown(
        Transcript transcript, IReadOnlyDictionary<long, string> titles, ImageScore? score, string? picture, string? takeaways)
    {
        var video = transcript.Video;
        // A JSON string is also a valid quoted YAML string.
        var note = new StringBuilder($"---\ntitle: {Sys.JsonString(video.Title)}\n");
        if (video.Url.StartsWith("http", StringComparison.Ordinal)) note.Append($"source: {Sys.JsonString(video.Url)}\n");
        if (video.Channel.Length > 0) note.Append($"channel: {Sys.JsonString(video.Channel)}\n");
        note.Append($"---\n\n# {video.Title}\n\n");

        if (video.Method == ImageMethod)
        {
            if (picture is not null) note.Append($"![{video.Title}]({picture.Replace(" ", "%20")})\n\n");
            if (score is not null)
            {
                note.Append($"## Score: {score.Score}%. {score.Certainty}\n\n{score.Identification}\n\n");
                foreach (var reason in score.Reasons) note.Append($"- {reason}\n");
                note.Append('\n');
            }
            note.Append("## Reverse prompt\n\n");
            note.Append(PlainText(transcript, titles));
            note.Append('\n');
            return note.ToString();
        }

        if (takeaways is not null) note.Append(takeaways).Append("\n\n");

        foreach (var passage in Captions.GroupIntoPassages(transcript.Segments, PassageMs))
        {
            var time = Captions.Clock(passage.StartMs);
            var link = Transcribe.LinkAt(video.Url, passage.StartMs);
            if (link != video.Url) time = $"[{time}]({link})";
            note.Append(titles.TryGetValue(passage.StartMs, out var title)
                ? $"## {time} {title}\n\n{passage.Text}\n\n"
                : $"## {time}\n\n{passage.Text}\n\n");
        }
        return note.ToString();
    }

    public static string SafeFileName(string title)
    {
        var cleaned = new StringBuilder(title.Length);
        foreach (var c in title) cleaned.Append("<>:\"/\\|?*".Contains(c) || char.IsControl(c) ? '_' : c);
        var trimmed = cleaned.ToString().Trim().TrimEnd('.');
        return trimmed.Length == 0 ? "transcript" : Sys.TakeChars(trimmed, 120);
    }

    internal static string UniqueVaultNotePath(Paths paths, string title)
    {
        var directory = paths.VaultProjectsDir();
        var parent = Sys.Parent(directory);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new MimicException($"The Mimic project folder is missing from the Obsidian vault: {directory}");
        }
        Sys.Io(() => Directory.CreateDirectory(directory), e => $"Could not create the Mimic Obsidian project folder: {e}");
        var name = SafeFileName(title);
        var candidate = Sys.Join(directory, $"{name}.md");
        var suffix = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Sys.Join(directory, $"{name} ({suffix}).md");
            suffix++;
        }
        return candidate;
    }

    /// <summary>
    /// A note can't show a picture from inside the library, so it gets its own copy next to it.
    /// Returns the copy's file name.
    /// </summary>
    internal static string? CopyPictureBeside(Transcript transcript, string note)
    {
        if (transcript.Video.Method != ImageMethod) return null;
        var source = transcript.Video.Url;
        var copy = Sys.WithExtension(note, Sys.Extension(source) ?? "png");
        Sys.Io(() => File.Copy(source, copy, true));
        return Sys.FileName(copy);
    }

    /// <summary>What the print script reads: the title, a line under it, a picture, and headed sections.</summary>
    internal static string PdfJob(
        Transcript transcript, IReadOnlyDictionary<long, string> titles, ImageScore? score, string? takeaways, string output)
    {
        var video = transcript.Video;
        var isImage = video.Method == ImageMethod;
        var sections = new List<(string Heading, string Text)>();
        if (score is not null)
        {
            var reasons = string.Concat(score.Reasons.Select(r => $"\n• {r}"));
            sections.Add(($"Score: {score.Score}%. {score.Certainty}", $"{score.Identification}{reasons}"));
        }
        if (takeaways is not null) sections.Add(("Key takeaways", takeaways));
        foreach (var passage in Captions.GroupIntoPassages(transcript.Segments, PassageMs))
        {
            string heading;
            if (isImage) heading = "Reverse prompt";
            else if (titles.TryGetValue(passage.StartMs, out var title)) heading = $"{Captions.Clock(passage.StartMs)}  {title}";
            else heading = Captions.Clock(passage.StartMs);
            sections.Add((heading, passage.Text));
        }
        var meta = isImage
            ? ""
            : $"{video.Channel} · Length {Captions.Clock(video.DurationSeconds * 1000)} · Transcribed in {video.TranscodeSeconds} seconds · {video.Url}".Trim();
        var list = string.Join(",", sections.Select(s => $"{{\"heading\":{Sys.JsonString(s.Heading)},\"text\":{Sys.JsonString(s.Text)}}}"));
        return $"{{\"image\":{(isImage ? Sys.JsonString(video.Url) : "null")},\"meta\":{Sys.JsonString(meta)},"
            + $"\"output\":{Sys.JsonString(output)},\"sections\":[{list}],\"title\":{Sys.JsonString(video.Title)}}}";
    }

    /// <summary>Prints to Microsoft Print to PDF without further prompts.</summary>
    public static void PrintPdf(
        Paths paths, Transcript transcript, IReadOnlyDictionary<long, string> titles, ImageScore? score, string? takeaways, string output)
    {
        Sys.Io(() => Directory.CreateDirectory(paths.WorkDir()));
        var jobFile = Sys.Join(paths.WorkDir(), $"pdf-{Sys.UniqueName()}.json");
        Sys.Io(() => File.WriteAllText(jobFile, PdfJob(transcript, titles, score, takeaways, output), Sys.Utf8));
        // The driver leaves an old file in place until it finishes, which would look like success.
        if (File.Exists(output) || Directory.Exists(output)) Sys.Io(() => File.Delete(output));

        var command = Tools.EncodedPowershell(Tools.Script("print_pdf.ps1"));
        command.Environment["MIMIC_PDF_JOB"] = jobFile;
        try
        {
            Tools.Run(new Job(), command, "Microsoft Print to PDF");
        }
        finally
        {
            Sys.RemoveFile(jobFile);
        }
    }
}
