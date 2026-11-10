// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Security.Cryptography;
using System.IO.Compression;
using System.Net;
using System.Text;
using Mimic.Core;

namespace Mimic.Tests;

/// <summary>Runs only when the real library is on this PC. It is copied to TEMP first and never opened in place.</summary>
public sealed class RealLibraryFactAttribute : FactAttribute
{
    internal static string LibraryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Mimic", "library.db");

    public RealLibraryFactAttribute()
    {
        if (!File.Exists(LibraryPath)) Skip = "There is no Mimic library on this PC.";
    }
}

/// <summary>Tests that use the internet or this PC's sound. Set MIMIC_LIVE_TESTS=1 to run them.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MIMIC_LIVE_TESTS") != "1") Skip = "Set MIMIC_LIVE_TESTS=1 to run tests that download or record.";
    }
}

public class LibraryTests
{
    [Fact]
    public void A_library_in_a_temp_folder_saves_reads_and_deletes()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "library.db");
        var saved = new Transcript(
            new Video
            {
                Id = "v1",
                Url = "https://youtu.be/x",
                LocalVideoPath = null,
                MediaHosts = ["youtu.be", "googlevideo.com"],
                Title = "A “title” — with ünïcode",
                Channel = "Channel",
                DurationSeconds = 61,
                TranscodeSeconds = 7,
                Language = "en",
                Method = "whisper",
                CreatedAt = 1_700_000_000,
            },
            [new Segment(0, 1500, "First words."), new Segment(40_000, 41_000, "Later words.")],
            null);
        using (var store = Store.Open(path))
        {
            store.Save(saved);
            store.SaveTakeaways("v1", "## Key takeaways\n- One");
            foreach (var choice in (ModelChoice[])
                [ModelChoice.Claude("claude-opus-5-5"), ModelChoice.OpenAI("gpt-6-luna"), ModelChoice.Server("http://localhost:11434/v1", "llava", "k\"ey"), ModelChoice.Default])
            {
                store.SaveModelChoice(choice);
                Assert.Equal(choice, store.GetModelChoice());
                Assert.Equal(choice, ModelChoice.FromJson(choice.ToJson()));
            }
        }
        // A second opening runs the migrations and cleanup again over the same file.
        using (var store = Store.Open(path))
        {
            var read = store.Get("v1");
            Assert.Equal(saved.Video, read.Video with { MediaHosts = saved.Video.MediaHosts });
            Assert.Equal(saved.Video.MediaHosts, read.Video.MediaHosts);
            Assert.Equal(saved.Segments, read.Segments);
            Assert.Equal("## Key takeaways\n- One", store.Takeaways("v1"));
            Assert.Equal("v1", Assert.Single(store.List()).Id);
            store.Delete("v1");
            Assert.Empty(store.List());
            Assert.Null(store.Takeaways("v1"));
            Assert.Throws<MimicException>(() => store.Get("v1"));
        }
    }

    [Fact]
    public void A_model_choice_keeps_the_stored_json_shape()
    {
        Assert.Equal("{\"kind\":\"default\"}", ModelChoice.Default.ToJson());
        Assert.Equal("{\"kind\":\"claude\",\"model\":\"m\"}", ModelChoice.Claude("m").ToJson());
        Assert.Equal("{\"kind\":\"openai\",\"model\":\"gpt-6-luna\"}", ModelChoice.OpenAI("gpt-6-luna").ToJson());
        Assert.Equal("{\"kind\":\"server\",\"address\":\"http://a\",\"model\":\"m\",\"key\":\"\"}", ModelChoice.Server("http://a", "m", "").ToJson());
    }

    [RealLibraryFact]
    public void A_copy_of_the_real_library_opens_and_lists_its_videos()
    {
        using var dir = new TempFolder();
        var copy = Path.Combine(dir.Path, "library.db");
        foreach (var suffix in (string[])["", "-wal", "-shm"])
        {
            var source = RealLibraryFactAttribute.LibraryPath + suffix;
            if (File.Exists(source)) CopyShared(source, copy + suffix);
        }
        using var store = Store.Open(copy);
        var videos = store.List();
        foreach (var video in videos)
        {
            var transcript = store.Get(video.Id);
            Assert.Equal(video.Id, transcript.Video.Id);
            Assert.False(string.IsNullOrEmpty(video.Method));
            _ = store.Titles(video.Id);
            _ = store.Score(video.Id);
            _ = store.Takeaways(video.Id);
            _ = MimicBackend.View(transcript, store.Titles(video.Id));
        }
        _ = store.GetModelChoice();
    }

    private static void CopyShared(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(target);
        input.CopyTo(output);
    }
}

public class BackendTests
{
    private static MimicBackend Backend(TempFolder dir) => new(
        new Paths(Path.Combine(dir.Path, "Mimic")),
        Path.Combine(dir.Path, "install", "downloads"),
        Path.Combine(dir.Path, "appdata", "downloads"),
        "9.9.9");

    private static void Seed(TempFolder dir, Transcript transcript)
    {
        Directory.CreateDirectory(Path.Combine(dir.Path, "Mimic"));
        using var store = Store.Open(Path.Combine(dir.Path, "Mimic", "library.db"));
        store.Save(transcript);
        store.SaveTitles(transcript.Video.Id, [(0, "Opening")]);
    }

    [Fact]
    public async Task Commands_work_over_a_temp_library()
    {
        using var dir = new TempFolder();
        Seed(dir, StoreTests.Transcript("a", "one two three"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "Mimic", "work", "left-behind"));
        using var backend = Backend(dir);

        Assert.Equal("9.9.9", backend.MimicVersion());
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "Mimic", "work")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "appdata", "downloads")));
        Assert.Equal(Path.Combine(dir.Path, "Mimic", "library.db"), backend.GetLibraryPath());
        Assert.Equal(["yt-dlp", "ffmpeg", "Whisper", "Whisper model"], backend.MissingTools());

        var view = backend.GetTranscript("a");
        Assert.Equal(3, view.Words);
        var passage = Assert.Single(view.Passages);
        Assert.Equal(("0:00", "Opening", "one two three", "https://example.com"), (passage.Clock, passage.Title, passage.Text, passage.Link));
        Assert.Null(view.Notice);

        Assert.Equal("A title cannot be empty.", Assert.Throws<MimicException>(() => backend.UpdateVideoTitle("a", "  ")).Message);
        Assert.Equal("New: name", backend.UpdateVideoTitle("a", " New: name ").Video.Title);
        Assert.Equal("New_ name.txt", backend.SuggestedFileName("a", "txt"));

        Assert.Equal("Unknown tab.", Assert.Throws<MimicException>(() => backend.CancelJob(2)).Message);
        Assert.Equal("Unknown tab.", Assert.Throws<MimicException>(() => backend.FinishCapture(-1)).Message);
        Assert.Equal("Unknown tab.", (await Assert.ThrowsAsync<MimicException>(() => backend.TitleVideoAsync(5, "a"))).Message);
        backend.CancelJob(0);
        backend.FinishCapture(1);

        Assert.Equal(ModelChoice.Default, backend.GetModel());
        Assert.Equal("Pick a Claude model.", Assert.Throws<MimicException>(() => backend.SetModel(ModelChoice.Claude(""))).Message);
        backend.SetModel(ModelChoice.OpenAI("gpt-6-luna"));
        Assert.Equal(ModelChoice.OpenAI("gpt-6-luna"), backend.GetModel());

        Assert.Equal("Only web links can be opened.", Assert.Throws<MimicException>(() => backend.OpenLink("file:///c:/x")).Message);
        Assert.Equal("https://example.com", backend.GetVideoPlaybackSource("a").Url);
        Assert.False(backend.GetVideoPlaybackSource("a").Local);

        var text = Path.Combine(dir.Path, "out.txt");
        Assert.Equal(text, await backend.ExportTranscriptAsync("a", "txt", false, text));
        Assert.Equal("[0:00] Opening\none two three\n\n", File.ReadAllText(text));
        var srt = Path.Combine(dir.Path, "out.srt");
        await backend.ExportTranscriptAsync("a", "srt", false, srt);
        Assert.Equal("1\n00:00:00,000 --> 00:00:01,000\none two three\n\n", File.ReadAllText(srt));
        var note = Path.Combine(dir.Path, "out.md");
        Assert.Equal(note, await backend.ExportTranscriptAsync("a", "md", false, note));
        Assert.Contains("one two three", File.ReadAllText(note));
        Assert.Null(await backend.ExportTranscriptAsync("a", "txt", false, null));
        Assert.Equal("Unknown format: docx", (await Assert.ThrowsAsync<MimicException>(() => backend.ExportTranscriptAsync("a", "docx", false, text))).Message);
        Assert.False(await backend.ExportPdfAsync("a", null));

        backend.DeleteVideo("a");
        Assert.Empty(backend.ListVideos());
    }

    [Fact]
    public void A_local_file_plays_from_its_real_path_and_a_picture_does_not_play()
    {
        using var dir = new TempFolder();
        var movie = Path.Combine(dir.Path, "movie.mp4");
        File.WriteAllBytes(movie, [1, 2, 3]);
        var picture = Path.Combine(dir.Path, "picture.png");
        File.WriteAllBytes(picture, [1, 2, 3]);
        var local = StoreTests.Transcript("local", "words");
        Seed(dir, local with { Video = local.Video with { Url = movie, Method = "whisper" } });
        Seed(dir, local with { Video = local.Video with { Id = "gone", Url = Path.Combine(dir.Path, "gone.mp4"), Method = "whisper" } });
        Seed(dir, local with { Video = local.Video with { Id = "image", Url = picture, Method = "image" } });
        using var backend = Backend(dir);

        var source = backend.GetVideoPlaybackSource("local");
        Assert.True(source.Local);
        Assert.StartsWith(@"\\?\", source.Url, StringComparison.Ordinal);
        Assert.True(File.Exists(MimicBackend.PlainPath(source.Url)));
        Assert.StartsWith("The original local video is unavailable: ", Assert.Throws<MimicException>(() => backend.GetVideoPlaybackSource("gone")).Message, StringComparison.Ordinal);
        Assert.EndsWith("(os error 2)", Assert.Throws<MimicException>(() => backend.GetVideoPlaybackSource("gone")).Message, StringComparison.Ordinal);
        Assert.Equal("Images do not have a video to play.", Assert.Throws<MimicException>(() => backend.GetVideoPlaybackSource("image")).Message);

        Assert.Equal(picture, backend.ImagePath("image"));
        Assert.Equal("data:image/png;base64,AQID", backend.ImageData("image"));
        backend.DeleteVideo("image");
        Assert.False(File.Exists(picture));
        backend.DeleteVideo("local");
        Assert.True(File.Exists(movie));
    }

    [Fact]
    public void A_missing_tool_is_reported_by_name()
    {
        using var dir = new TempFolder();
        var error = Assert.Throws<MimicException>(() =>
            Tools.Run(new Job(), new ToolCommand(Path.Combine(dir.Path, "nothing.exe")) { WorkingDirectory = dir.Path }, "yt-dlp"));
        Assert.Equal($"Could not start yt-dlp at '{Path.Combine(dir.Path, "nothing.exe")}' from '{dir.Path}': program not found", error.Message);
        Assert.Equal("Tool failed: ERROR: second", Tools.ToolError("Tool", "ERROR: first\nERROR: second\nlast line\n"));
        Assert.Equal("Tool failed: it stopped without saying why", Tools.ToolError("Tool", " \n"));
    }

    [Fact]
    public void A_tool_runs_hidden_takes_input_and_stops_when_cancelled()
    {
        using var dir = new TempFolder();
        var input = Path.Combine(dir.Path, "in.txt");
        File.WriteAllText(input, "hello from stdin\n");
        var echoed = Tools.RunWithInput(new Job(), new ToolCommand("findstr").Args("hello"), "findstr", input);
        Assert.Equal("hello from stdin", echoed.Trim());
        Assert.Equal("hello from memory", Tools.RunWithText(new Job(), new ToolCommand("findstr").Args("hello"), "findstr", "hello from memory\n").Trim());

        var failed = Assert.Throws<MimicException>(() =>
            Tools.Run(new Job(), new ToolCommand("cmd").Args("/c", "echo ERROR: broke 1>&2 & exit 3"), "Thing"));
        Assert.Equal("Thing failed: ERROR: broke", failed.Message);

        var job = new Job();
        Assert.True(job.Start());
        var running = Task.Run(() => Tools.Run(job, new ToolCommand("ping").Args("-n", "60", "127.0.0.1"), "ping"));
        Thread.Sleep(1000);
        job.RequestCancel();
        var cancelled = Assert.Throws<AggregateException>(() => running.Wait(TimeSpan.FromSeconds(20)));
        Assert.Equal("Cancelled.", Assert.IsType<MimicException>(cancelled.InnerException).Message);

        var slow = Assert.Throws<MimicException>(() =>
            Tools.RunWithTimeout(new Job(), new ToolCommand("ping").Args("-n", "60", "127.0.0.1"), "ping", null, TimeSpan.FromSeconds(1)));
        Assert.Equal("yt-dlp failed: timed out reading the video's details after 1 seconds.", slow.Message);
    }

    [Fact]
    public void The_scripts_travel_inside_the_library()
    {
        Assert.Contains("MIMIC_PDF_JOB", Tools.Script("print_pdf.ps1"), StringComparison.Ordinal);
        Assert.Contains("process_id", Tools.Script("pick_video.ps1"), StringComparison.Ordinal);
        var command = Tools.EncodedPowershell("exit 0");
        Assert.Equal(["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("exit 0"))], command.Arguments);
    }

    [Fact]
    public void Summary_batches_cover_every_word()
    {
        var segments = Enumerable.Range(0, 400).Select(i => new Segment(i * 1000, i * 1000 + 900, string.Join(' ', Enumerable.Repeat($"word{i}", 20)))).ToList();
        var batches = Ai.TranscriptExcerptBatches(segments);
        Assert.True(batches.Count > 1);
        Assert.All(batches, batch => Assert.True(batch.Length <= Ai.SummaryBatchChars));
        Assert.Equal(400 * 20, batches.Sum(batch => batch.Split(' ', '\n').Count(word => word.StartsWith("word", StringComparison.Ordinal))));
        Assert.StartsWith("[0:00] word0 ", batches[0], StringComparison.Ordinal);
    }

    [LiveFact]
    public void Recording_this_silent_process_reports_no_sound()
    {
        using var dir = new TempFolder();
        var job = new Job();
        Assert.True(job.Start());
        _ = Task.Run(() =>
        {
            Thread.Sleep(1500);
            job.RequestFinishCapture();
        });
        var error = Assert.Throws<MimicException>(() => Capture.Record(job, (uint)Environment.ProcessId, Path.Combine(dir.Path, "audio.wav")));
        Assert.Equal("No sound came from that window. Play the video first, then press Finish when it ends.", error.Message);
    }
}

public class OwnServerTests
{
    /// <summary>Runs the real curl against a server on this PC that answers once.</summary>
    [Fact]
    public async Task The_key_reaches_the_server_without_being_written_to_a_file()
    {
        using var dir = new TempFolder();
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var files = Array.Empty<string>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var request = new StringBuilder();
            var buffer = new byte[65536];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                request.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var text = request.ToString();
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) continue;
                var length = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"(?i)content-length: (\d+)").Groups[1].Value);
                if (Encoding.UTF8.GetByteCount(text[(end + 4)..]) >= length) break;
            }
            files = Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray()!;
            var body = """{"choices":[{"message":{"content":"hello back"}}]}""";
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
            return request.ToString();
        });

        var reply = Ai.AskServer(new Job(), new Paths(dir.Path), ModelChoice.Server($"http://127.0.0.1:{port}/v1", "some-model", "test key \"1\""), "hello", null);
        var seen = await server;
        listener.Stop();

        Assert.Equal("hello back", reply);
        Assert.StartsWith("POST /v1/chat/completions ", seen, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer test key \"1\"\r\n", seen, StringComparison.Ordinal);
        Assert.Contains("\"some-model\"", seen, StringComparison.Ordinal);
        Assert.Equal(["body.json"], files);
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories));
    }
}

public class ToolInstallerTests
{
    private sealed class FakeWeb(Func<Uri, byte[]?> pages) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.ToString());
            UserAgent = request.Headers.UserAgent.ToString();
            var body = pages(request.RequestUri);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return bytes.ToArray();
    }

    private static string[] Files(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).Order().ToArray();

    [Fact]
    public async Task Installs_each_tool_where_mimic_looks_for_it()
    {
        using var dir = new TempFolder();
        var yt = "yt"u8.ToArray();
        var ffmpeg = Zip(
            ("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe", "ff"), ("ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe", "probe"),
            ("ffmpeg-master-latest-win64-gpl/bin/ffplay.exe", "play"), ("ffmpeg-master-latest-win64-gpl/doc/readme.txt", "doc"));
        var whisper = Zip(("Release/whisper-cli.exe", "cli"), ("Release/whisper.dll", "dll"), ("Release/../../escape.txt", "no"), ("readme.txt", "no"));
        // The newest whisper release has no Windows build, and the next lists one without a checksum; neither may be used.
        var releases = $$"""
            [{"tag_name":"v9","assets":[]},
             {"tag_name":"b3","assets":[{"name":"whisper-bin-x64.zip","browser_download_url":"https://example.test/unchecked.zip"}]},
             {"tag_name":"b2","assets":[{"name":"other.zip","browser_download_url":"https://example.test/other.zip","digest":"sha256:{{Sha(yt)}}"},
                                        {{Asset("whisper-bin-x64.zip", "https://example.test/w.zip", whisper)}}]},
             {"tag_name":"b1","assets":[{{Asset("whisper-bin-x64.zip", "https://example.test/old.zip", yt)}}]}]
            """;
        var web = new FakeWeb(uri => uri.ToString() switch
        {
            ToolInstaller.YtDlpReleaseUrl => Release(Asset("yt-dlp.exe", "https://example.test/yt.exe", yt)),
            "https://example.test/yt.exe" => yt,
            ToolInstaller.FfmpegReleaseUrl => Release(Asset("ffmpeg-master-latest-win64-gpl.zip", "https://example.test/ff.zip", ffmpeg)),
            "https://example.test/ff.zip" => ffmpeg,
            ToolInstaller.WhisperReleasesUrl => Encoding.UTF8.GetBytes(releases),
            "https://example.test/w.zip" => whisper,
            _ => null,
        });
        using var installer = new ToolInstaller(dir.Path, web);
        var seen = new List<(string message, double? fraction)>();
        var progress = new Collect(seen);

        await installer.InstallMissingToolsAsync(progress, CancellationToken.None);

        Assert.Equal("Mimic", web.UserAgent);
        Assert.DoesNotContain(ToolInstaller.ModelUrl, web.Asked);
        Assert.Equal(
            [@"tools\ffmpeg\ffmpeg.exe", @"tools\ffmpeg\ffprobe.exe", @"tools\whisper\Release\whisper-cli.exe", @"tools\whisper\Release\whisper.dll", @"tools\yt-dlp\yt-dlp.exe"],
            Files(dir.Path));
        Assert.Equal("yt", File.ReadAllText(Path.Combine(dir.Path, @"tools\yt-dlp\yt-dlp.exe")));
        Assert.Equal("probe", File.ReadAllText(Path.Combine(dir.Path, @"tools\ffmpeg\ffprobe.exe")));
        Assert.Equal(["Whisper model"], new Paths(dir.Path).MissingTools());
        Assert.Contains(("Downloading yt-dlp…", null), seen);
        Assert.Contains(("yt-dlp is installed.", 1.0), seen);
        Assert.Contains(seen, entry => entry.fraction == 1.0 && entry.message.StartsWith("Downloading ffmpeg: ", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, entry => entry.message.Contains('~'));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Asset(string name, string url, byte[] bytes) =>
        $$"""{"name":"{{name}}","browser_download_url":"{{url}}","digest":"sha256:{{Sha(bytes)}}"}""";

    private static byte[] Release(string asset) => Encoding.UTF8.GetBytes($$"""{"tag_name":"t","assets":[{{asset}}]}""");

    [Fact]
    public async Task A_program_that_does_not_match_its_published_checksum_is_deleted()
    {
        using var dir = new TempFolder();
        var published = Release(Asset("yt-dlp.exe", "https://example.test/yt.exe", "the real one"u8.ToArray()));
        var swapped = new FakeWeb(uri => uri.ToString() switch
        {
            ToolInstaller.YtDlpReleaseUrl => published,
            "https://example.test/yt.exe" => "something else"u8.ToArray(),
            _ => null,
        });
        using var installer = new ToolInstaller(dir.Path, swapped);
        var error = await Assert.ThrowsAsync<MimicException>(() => installer.InstallAsync("yt-dlp", null, CancellationToken.None));
        Assert.Equal("The yt-dlp download did not match its published checksum, so it was deleted. Try again.", error.Message);
        Assert.Empty(Files(dir.Path));

        // No checksum from GitHub: nothing is downloaded at all, not even from the plain address.
        var silent = new FakeWeb(uri => uri.ToString() switch
        {
            ToolInstaller.FfmpegReleaseUrl => """{"tag_name":"t","assets":[{"name":"ffmpeg-master-latest-win64-gpl.zip","browser_download_url":"https://example.test/ff.zip"}]}"""u8.ToArray(),
            _ => "anything"u8.ToArray(),
        });
        using var blind = new ToolInstaller(dir.Path, silent);
        error = await Assert.ThrowsAsync<MimicException>(() => blind.InstallAsync("ffmpeg", null, CancellationToken.None));
        Assert.Equal("Could not install ffmpeg: GitHub did not give the checksum for its download, so nothing was downloaded. Try again later.", error.Message);
        Assert.Equal([ToolInstaller.FfmpegReleaseUrl], silent.Asked);
        Assert.Empty(Files(dir.Path));

        // Whisper with no release list falls back to the pinned build, which must match the pinned checksum.
        using var pinned = new ToolInstaller(dir.Path, new FakeWeb(uri => uri.ToString() == ToolInstaller.WhisperFallbackUrl ? "not that build"u8.ToArray() : null));
        error = await Assert.ThrowsAsync<MimicException>(() => pinned.InstallAsync("Whisper", null, CancellationToken.None));
        Assert.Equal("The Whisper download did not match its published checksum, so it was deleted. Try again.", error.Message);
        Assert.Empty(Files(dir.Path));
    }

    private sealed class Collect(List<(string message, double? fraction)> seen) : IProgress<(string message, double? fraction)>
    {
        public void Report((string message, double? fraction) value)
        {
            lock (seen) seen.Add(value);
        }
    }

    [Fact]
    public async Task A_damaged_model_is_deleted_and_reported()
    {
        using var dir = new TempFolder();
        using var installer = new ToolInstaller(dir.Path, new FakeWeb(uri => uri.ToString() == ToolInstaller.ModelUrl ? "not the model"u8.ToArray() : null));
        var error = await Assert.ThrowsAsync<MimicException>(() => installer.InstallAsync("Whisper model", null, CancellationToken.None));
        Assert.Equal("The Whisper model download was damaged (its checksum did not match), so it was deleted. Try again.", error.Message);
        Assert.Empty(Files(dir.Path));
    }

    [Fact]
    public async Task A_failed_or_cancelled_download_leaves_nothing_behind()
    {
        using var dir = new TempFolder();
        using var installer = new ToolInstaller(dir.Path, new FakeWeb(_ => null));
        var error = await Assert.ThrowsAsync<MimicException>(() => installer.InstallAsync("yt-dlp", null, CancellationToken.None));
        Assert.StartsWith("Could not install yt-dlp: ", error.Message, StringComparison.Ordinal);
        using var broken = new ToolInstaller(dir.Path, new FakeWeb(uri => uri.ToString() == ToolInstaller.YtDlpReleaseUrl ? Release(Asset("yt-dlp.exe", "https://example.test/gone.exe", "yt"u8.ToArray())) : null));
        Assert.StartsWith("Could not install yt-dlp: ", (await Assert.ThrowsAsync<MimicException>(() => broken.InstallAsync("yt-dlp", null, CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Equal("Unknown tool: curl", (await Assert.ThrowsAsync<MimicException>(() => installer.InstallAsync("curl", null, CancellationToken.None))).Message);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stopped = new ToolInstaller(dir.Path, new FakeWeb(_ => "yt"u8.ToArray()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.InstallAsync("yt-dlp", null, cancelled.Token));
        Assert.Empty(Files(dir.Path));
    }

    [LiveFact]
    public async Task Really_downloads_yt_dlp_and_whisper_into_temp()
    {
        using var dir = new TempFolder();
        using var installer = new ToolInstaller(dir.Path);
        var seen = new List<(string message, double? fraction)>();
        await installer.InstallAsync("yt-dlp", new Collect(seen), CancellationToken.None);
        await installer.InstallAsync("Whisper", new Collect(seen), CancellationToken.None);
        var paths = new Paths(dir.Path);
        Assert.True(new FileInfo(paths.YtDlp()).Length > 5_000_000);
        Assert.True(new FileInfo(paths.Whisper()).Length > 10_000);
        Assert.True(File.Exists(Path.Combine(dir.Path, @"tools\whisper\Release\whisper.dll")));
        Assert.Equal(["ffmpeg", "Whisper model"], paths.MissingTools());
        Assert.DoesNotContain(Files(dir.Path), file => file.EndsWith(".part", StringComparison.Ordinal));
        Assert.Contains(seen, entry => entry.fraction is > 0 and < 1);
    }

    /// <summary>The two large downloads are only asked for their first bytes.</summary>
    [LiveFact]
    public async Task The_ffmpeg_and_model_addresses_answer()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mimic");
        foreach (var url in (string[])[ToolInstaller.FfmpegUrl, ToolInstaller.ModelUrl, ToolInstaller.WhisperFallbackUrl])
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new(0, 1023);
            using var reply = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.PartialContent, reply.StatusCode);
            Assert.True(reply.Content.Headers.ContentRange!.Length > 1_000_000);
        }
    }
}
