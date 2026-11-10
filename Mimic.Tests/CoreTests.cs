// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
// Tests for the backend: library, transcripts, exports and tools.
using System.Text;
using System.Text.Json;
using Mimic.Core;

namespace Mimic.Tests;

public class CaptionsTests
{
    private const string Rolling = "WEBVTT\nKind: captions\nLanguage: en\n\n"
        + "00:00:00.160 --> 00:00:02.389 align:start position:0%\n \n"
        + "hello<00:00:00.480><c> world</c>\n\n"
        + "00:00:02.389 --> 00:00:02.399 align:start position:0%\n"
        + "hello world\n \n\n"
        + "00:00:02.399 --> 00:00:04.710 align:start position:0%\n"
        + "hello world\n"
        + "this<00:00:02.800><c> is</c><00:00:03.000><c> new</c>\n\n"
        + "00:00:04.710 --> 00:00:04.720 align:start position:0%\n"
        + "this is new\n \n";

    [Fact]
    public void Rolling_captions_keep_each_line_once()
    {
        var segments = Captions.ParseVtt(Rolling, true);
        Assert.Equal(["hello world", "this is new"], segments.Select(s => s.Text));
        Assert.Equal(160, segments[0].StartMs);
        Assert.Equal(2399, segments[1].StartMs);
    }

    [Fact]
    public void Manual_captions_keep_a_repeated_line()
    {
        var vtt = "WEBVTT\n\n00:01.000 --> 00:02.000\nYes.\n\n00:02.000 --> 00:03.000\nYes.\n";
        Assert.Equal(2, Captions.ParseVtt(vtt, false).Count);
    }

    [Fact]
    public void Handles_crlf_tags_entities_and_hours()
    {
        var vtt = "WEBVTT\r\n\r\n1\r\n01:00:01.500 --> 01:00:02.000\r\n<v Bob>Tom &amp; Jerry&#39;s &gt; all</v>\r\n";
        var segments = Captions.ParseVtt(vtt, false);
        Assert.Equal("Tom & Jerry's > all", segments[0].Text);
        Assert.Equal(3_601_500, segments[0].StartMs);
    }

    [Fact]
    public void Escaped_styling_tags_are_removed_but_real_comparisons_stay()
    {
        var vtt = "WEBVTT\n\n00:01.000 --> 00:02.000\n&lt;i&gt;Slowly&lt;/i&gt; 1 &lt; 2\n";
        Assert.Equal("Slowly 1 < 2", Captions.ParseVtt(vtt, false)[0].Text);
    }

    [Fact]
    public void Empty_and_malformed_input_gives_nothing()
    {
        Assert.Empty(Captions.ParseVtt("", true));
        Assert.Empty(Captions.ParseVtt("WEBVTT\n\nnot --> a timing\ntext\n", false));
    }

    [Fact]
    public void Whisper_json_skips_blank_items_and_reads_language()
    {
        var json = """
            {"result":{"language":"en"},"transcription":[
            {"timestamps":{"from":"00:00:00,000","to":"00:00:02,000"},"offsets":{"from":0,"to":2000},"text":" Hi there"},
            {"timestamps":{"from":"00:00:02,000","to":"00:00:03,000"},"offsets":{"from":2000,"to":3000},"text":"  "}]}
            """;
        var (segments, language) = Captions.ParseWhisperJson(json);
        Assert.Single(segments);
        Assert.Equal("Hi there", segments[0].Text);
        Assert.Equal("en", language);
        Assert.Throws<MimicException>(() => Captions.ParseWhisperJson("not json"));
    }

    [Fact]
    public void Passages_group_by_time_and_keep_first_timestamp()
    {
        static Segment Line(long start, string text) => new(start, start + 1000, text);
        var passages = Captions.GroupIntoPassages([Line(0, "a"), Line(5000, "b"), Line(20000, "c")], 15000);
        Assert.Equal(2, passages.Count);
        Assert.Equal("a b", passages[0].Text);
        Assert.Equal(6000, passages[0].EndMs);
        Assert.Equal(20000, passages[1].StartMs);
    }

    [Fact]
    public void Srt_and_clock_formats()
    {
        var srt = Captions.ToSrt([new Segment(3_661_001, 3_662_000, "x")]);
        Assert.Equal("1\n01:01:01,001 --> 01:01:02,000\nx\n\n", srt);
        Assert.Equal("1:05", Captions.Clock(65_000));
        Assert.Equal("1:01:05", Captions.Clock(3_665_000));
    }
}

public class CaptureTests
{
    [Fact]
    public void Only_audible_stretches_count()
    {
        Assert.False(Capture.Audible([0, 0, 0, 0]));
        Assert.False(Capture.Audible(BitConverter.GetBytes((short)5)));
        Assert.True(Capture.Audible([0, 0, 0x00, 0x10]));
        Assert.True(Capture.Audible(BitConverter.GetBytes((short)-2000)));
    }

    [Fact]
    public void Wav_header_describes_the_audio()
    {
        var header = Capture.WavHeader(32_000);
        Assert.Equal(44, header.Length);
        Assert.Equal("RIFF"u8.ToArray(), header[..4]);
        Assert.Equal(16_000u, BitConverter.ToUInt32(header, 24));
        Assert.Equal(32_000u, BitConverter.ToUInt32(header, 40));
    }
}

public class EmbeddedTests
{
    private static Target Target(string[] frames, string address) =>
        new() { Frames = frames, Address = address, Title = "", ProcessId = 1 };

    [Fact]
    public void Tries_the_clicked_frame_first_and_each_address_once()
    {
        var both = Target(["https://player.example/1", "https://site.example/page"], "https://site.example/page");
        Assert.Equal(["https://player.example/1", "https://site.example/page"], both.Links());
        Assert.Equal("https://site.example/page", both.Page());
    }

    [Fact]
    public void An_app_with_no_addresses_goes_straight_to_listening()
    {
        var app = Target([], "");
        Assert.Empty(app.Links());
        Assert.Null(app.Page());
    }

    [Fact]
    public void Reads_the_crosshair_reply()
    {
        var reply = """{"process_id":42,"address":"https://a.example/","frames":["https://a.example/"],"title":"A"}""";
        var parsed = Core.Target.FromJson(reply);
        Assert.Equal(42u, parsed.ProcessId);
        Assert.Equal(["https://a.example/"], parsed.Links());
    }
}

public class TranscribeTests
{
    [Fact]
    public void Rejects_non_links()
    {
        Assert.Throws<MimicException>(() => Transcribe.ValidateUrl("--exec calc"));
        Assert.Throws<MimicException>(() => Transcribe.ValidateUrl("file:///c:/x"));
        Assert.Equal("https://youtu.be/x", Transcribe.ValidateUrl("  https://youtu.be/x "));
    }

    [Fact]
    public void Timestamp_links()
    {
        Assert.Equal("https://www.youtube.com/watch?v=a&t=65s", Transcribe.LinkAt("https://www.youtube.com/watch?v=a", 65_900));
        Assert.Equal("https://youtu.be/a?t=1s", Transcribe.LinkAt("https://youtu.be/a", 1000));
        Assert.Equal("https://vimeo.com/1", Transcribe.LinkAt("https://vimeo.com/1", 1000));
    }

    [Fact]
    public void A_second_job_cannot_start_while_one_runs()
    {
        var job = new Job();
        Assert.True(job.Start());
        Assert.False(job.Start());
        job.Finish();
        Assert.True(job.Start());
    }
}

public class AiTests
{
    [Fact]
    public void A_server_gets_the_image_instead_of_a_file_to_read()
    {
        Assert.True(Ai.ImagePrompt.StartsWith(Ai.ReadImage, StringComparison.Ordinal) && Ai.ScorePrompt.StartsWith(Ai.ReadImage, StringComparison.Ordinal));
        var request = Ai.ServerRequest(" llava ", Ai.ImagePrompt, "data:image/png;base64,AA");
        Assert.Equal("llava", (string?)request["model"]);
        var content = request["messages"]![0]!["content"]!;
        Assert.StartsWith(Ai.ImageAttached, (string?)content[0]!["text"], StringComparison.Ordinal);
        Assert.Equal("data:image/png;base64,AA", (string?)content[1]!["image_url"]!["url"]);
        Assert.Equal("plain", (string?)Ai.ServerRequest("m", "plain", null)["messages"]![0]!["content"]);
    }

    [Fact]
    public void Reads_a_server_reply()
    {
        Assert.Equal("Hi", Ai.ServerReply("""{"choices":[{"message":{"content":"Hi"}}]}"""));
        Assert.Equal("The server's reply had no text in it.", Assert.Throws<MimicException>(() => Ai.ServerReply("""{"error":"nope"}""")).Message);
        Assert.Throws<MimicException>(() => Ai.ServerReply("not json"));
    }

    [Fact]
    public void Checks_a_model_choice()
    {
        ModelChoice.Default.Check();
        Assert.Throws<MimicException>(() => ModelChoice.Claude(" ").Check());
        Assert.Throws<MimicException>(() => ModelChoice.OpenAI(" ").Check());
        ModelChoice.OpenAI("gpt-6-luna").Check();
        ModelChoice.Server("http://localhost:11434/v1", "llava", "").Check();
        Assert.Throws<MimicException>(() => ModelChoice.Server("file:///c:/secrets", "llava", "").Check());
        Assert.Throws<MimicException>(() => ModelChoice.Server("http://localhost:11434/v1", "", "").Check());
    }

    [Fact]
    public void Reads_titles_around_stray_text()
    {
        var reply = "```json\n{\"1\": \"One\", \"2\": \"Two\"}\n```";
        Assert.Equal(["One", "Two"], Ai.ParseTitles(reply, 2));
    }

    [Fact]
    public void A_skipped_section_keeps_the_others_in_place()
    {
        Assert.Equal(["One", "", "Three"], Ai.ParseTitles("{\"1\": \"One\", \"3\": \"Three\"}", 3));
        Assert.Equal("The model did not return section titles.", Assert.Throws<MimicException>(() => Ai.ParseTitles("no titles here", 2)).Message);
    }

    [Fact]
    public void Reads_a_score_and_keeps_it_in_range()
    {
        var reply = "```json\n{\"score\": 140, \"identification\": \"Likely Flux\", \"reasons\": [\"Melted text\"]}\n```";
        var score = Ai.ParseScore(reply);
        Assert.Equal(100, score.Score);
        Assert.Equal("Likely Flux", score.Identification);
        Assert.Equal(["Melted text"], score.Reasons);
        Assert.Equal("The model did not return a score.", Assert.Throws<MimicException>(() => Ai.ParseScore("I can't tell")).Message);
    }

    [Fact]
    public void A_score_reads_as_one_of_six_tiers()
    {
        static string Tier(long score) => new ImageScore(score, "", []).Certainty;
        Assert.Equal("Almost certainly AI-generated", Tier(100));
        Assert.Equal("Almost certainly AI-generated", Tier(90));
        Assert.Equal("Probably AI-generated", Tier(89));
        Assert.Equal("Leaning AI-generated", Tier(50));
        Assert.Equal("Leaning not AI-generated", Tier(49));
        Assert.Equal("Probably not AI-generated", Tier(10));
        Assert.Equal("Almost certainly not AI-generated", Tier(3));
    }

    [Fact]
    public void Finds_generator_names_and_camera_details_in_a_file()
    {
        var file = new List<byte>();
        file.AddRange([0x89, .. "PNG"u8, 0x01, 0x02, .. "parameters\0a castle, Steps: 20"u8, 0x00, 0xff]);
        file.AddRange([.. "Exif\0\0"u8, 0x01, 0x02, .. "Canon\0EOS R5\0ab\0"u8]);
        var clues = Ai.FileClues(file.ToArray());
        Assert.Contains("parameters", clues);
        Assert.Contains("EXIF text: Canon | EOS R5", clues);
        Assert.Empty(Ai.FileClues("nothing to see"u8));
    }
}

public class ExportTests
{
    private static Transcript Entry(string url, string method, string text) => new(
        new Video
        {
            Id = "a",
            Url = url,
            Title = "A \"quoted\" title",
            Channel = "Channel",
            DurationSeconds = 70,
            TranscodeSeconds = 13,
            Language = "en",
            Method = method,
            CreatedAt = 1,
        },
        [new Segment(0, 1000, text), new Segment(65_000, 66_000, "Later words.")],
        null);

    [Fact]
    public void A_note_has_frontmatter_and_a_heading_per_section()
    {
        var titles = new Dictionary<long, string> { [0] = "Opening idea" };
        var note = Exports.ToMarkdown(Entry("https://youtu.be/x", "whisper", "First words."), titles, null, null, null);
        Assert.StartsWith("---\ntitle: \"A \\\"quoted\\\" title\"\nsource: \"https://youtu.be/x\"\nchannel: \"Channel\"\n---\n\n# A \"quoted\" title\n\n", note, StringComparison.Ordinal);
        Assert.Contains("## [0:00](https://youtu.be/x?t=0s) Opening idea\n\nFirst words.\n\n", note, StringComparison.Ordinal);
        Assert.Contains("## [1:05](https://youtu.be/x?t=65s)\n\nLater words.\n\n", note, StringComparison.Ordinal);

        // Timestamps only link where the site can jump to them.
        var other = Exports.ToMarkdown(Entry("https://example.com/page", "capture", "First words."), titles, null, null, null);
        Assert.Contains("## 0:00 Opening idea\n\n", other, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_note_has_the_image_score_and_prompt()
    {
        var score = new ImageScore(90, "Likely Flux", ["Melted text"]);
        var picture = Entry("C:\\library\\a.png", "image", "A red barn.");
        picture = picture with { Segments = [picture.Segments[0]] };
        var note = Exports.ToMarkdown(picture, new Dictionary<long, string>(), score, "my barn.png", null);
        Assert.DoesNotContain("source:", note, StringComparison.Ordinal);
        Assert.Contains("(my%20barn.png)\n\n## Score: 90%. Almost certainly AI-generated\n\nLikely Flux\n\n- Melted text\n\n## Reverse prompt\n\nA red barn.\n", note, StringComparison.Ordinal);
    }

    [Fact]
    public void File_names_are_safe_on_windows()
    {
        Assert.Equal("What_ is_this_ _really_", Exports.SafeFileName("What: is/this? *really*."));
        Assert.Equal("transcript", Exports.SafeFileName("..."));
    }

    // The print job and the text export.
    [Fact]
    public void The_pdf_job_holds_what_the_print_script_reads()
    {
        var titles = new Dictionary<long, string> { [0] = "Opening idea" };
        using var job = JsonDocument.Parse(Exports.PdfJob(Entry("https://youtu.be/x", "whisper", "First words."), titles, null, "Takeaway", "C:\\out\\a.pdf"));
        var root = job.RootElement;
        Assert.Equal("A \"quoted\" title", root.GetProperty("title").GetString());
        Assert.Equal("Channel · Length 1:10 · Transcribed in 13 seconds · https://youtu.be/x", root.GetProperty("meta").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("image").ValueKind);
        Assert.Equal("C:\\out\\a.pdf", root.GetProperty("output").GetString());
        var sections = root.GetProperty("sections");
        Assert.Equal("Key takeaways", sections[0].GetProperty("heading").GetString());
        Assert.Equal("0:00  Opening idea", sections[1].GetProperty("heading").GetString());
        Assert.Equal("1:05", sections[2].GetProperty("heading").GetString());
    }

    [Fact]
    public void Plain_text_has_a_clock_and_title_per_section()
    {
        var titles = new Dictionary<long, string> { [0] = "Opening idea" };
        Assert.Equal(
            "[0:00] Opening idea\nFirst words.\n\n[1:05] Later words.\n\n",
            Exports.PlainText(Entry("https://youtu.be/x", "whisper", "First words."), titles));
    }
}

public class StoreTests
{
    internal static Transcript Transcript(string id, string text) => new(
        new Video
        {
            Id = id,
            Url = "https://example.com",
            Title = "Title",
            Channel = "Channel",
            DurationSeconds = 10,
            Language = "en",
            Method = "captions",
            CreatedAt = 1,
        },
        [new Segment(0, 1000, text)],
        null);

    [Fact]
    public void Saves_replaces_and_deletes()
    {
        using var dir = new TempFolder();
        using (var store = Store.Open(Path.Combine(dir.Path, "test.db")))
        {
            store.Save(Transcript("a", "first"));
            store.Save(Transcript("a", "second"));
            Assert.Single(store.List());
            Assert.Equal("second", store.Get("a").Segments[0].Text);

            store.SaveTitles("a", [(0, "Opening")]);
            Assert.Equal("Opening", store.Titles("a")[0]);

            Assert.Null(store.Score("a"));
            store.SaveScore("a", new ImageScore(90, "Likely Flux", ["Melted text"]));
            Assert.Equal(90, store.Score("a")!.Score);

            Assert.Equal(ModelChoice.Default, store.GetModelChoice());
            var choice = ModelChoice.Claude("claude-opus-5-5");
            store.SaveModelChoice(choice);
            Assert.Equal(choice, store.GetModelChoice());

            store.Delete("a");
            Assert.Empty(store.Titles("a"));
            Assert.Null(store.Score("a"));
            Assert.Throws<MimicException>(() => store.Get("a"));
        }
    }
}

/// <summary>A folder in TEMP that is removed afterwards. Nothing here touches the real library.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mimic-test-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
