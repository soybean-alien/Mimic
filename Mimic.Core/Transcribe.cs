// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mimic.Core;

/// <summary>The transcription job: the video's audio through yt-dlp, then Whisper.</summary>
public static class Transcribe
{
    private static string? Str(JsonElement info, string key) =>
        info.ValueKind == JsonValueKind.Object && info.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? Field(JsonElement info, string key) =>
        info.ValueKind == JsonValueKind.Object && info.TryGetProperty(key, out var value) ? value : null;

    /// <summary>The host of a web address, or nothing if it is not one.</summary>
    internal static string? Host(string address, bool webOnly)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed)) return null;
        if (webOnly && parsed.Scheme is not ("http" or "https")) return null;
        var host = parsed.IdnHost;
        return string.IsNullOrEmpty(host) ? null : host;
    }

    private static ToolCommand YtDlp(Paths paths, string? referer)
    {
        var command = new ToolCommand(paths.YtDlp()).Args("--ignore-config", "--no-playlist", "--no-warnings");
        command.Args("--ffmpeg-location", paths.FfmpegDir());
        if (referer is not null) command.Args("--referer", referer);
        return command;
    }

    /// <summary>Runs Whisper over a 16 kHz mono WAV, returning segments and the language it heard.</summary>
    internal static (List<Segment> Segments, string? Language) RunWhisper(Job job, Paths paths, string audio, string work, Action<string> report)
    {
        report("Transcribing with Whisper…");
        var basePath = Sys.Join(work, "whisper");
        var whisper = paths.Whisper();
        var command = new ToolCommand(whisper);
        if (Sys.Parent(whisper) is { } dir) command.WorkingDirectory = dir;
        command.Args("-m", paths.Model(), "-f", audio).Args("-l", "auto", "-bs", "1", "-bo", "1", "-oj", "-of").Args(basePath);
        Tools.Run(job, command, "Whisper");

        var bytes = Sys.Io(() => File.ReadAllBytes(Sys.WithExtension(basePath, "json")), e => $"Whisper wrote no result: {e}");
        return Captions.ParseWhisperJson(Encoding.UTF8.GetString(bytes));
    }

    private static ToolCommand AudioDownload(Paths paths, string? referer, string work, string infoJson) =>
        YtDlp(paths, referer)
            .Args("-f", "bestaudio/best", "-x", "--audio-format", "wav")
            .Args("--postprocessor-args", "ffmpeg:-ar 16000 -ac 1")
            .Args("-o", Sys.Join(work, "audio.%(ext)s"))
            .Args("--load-info-json", infoJson);

    private static (List<Segment> Segments, string? Language) TranscribeWithWhisper(
        Job job, Paths paths, string infoJson, string? localVideo, string? referer, string work, Action<string> report)
    {
        var audio = Sys.Join(work, "audio.wav");
        if (localVideo is not null)
        {
            report("Preparing audio from the downloaded video…");
            var command = new ToolCommand(Sys.Join(paths.FfmpegDir(), "ffmpeg.exe"))
                .Args("-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", localVideo)
                .Args("-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", audio);
            try
            {
                Tools.Run(job, command, "ffmpeg");
            }
            catch (MimicException error)
            {
                if (job.Cancelled || error.Message == Tools.Cancelled) throw new MimicException(Tools.Cancelled);
                report("The local copy could not be read for audio; downloading audio separately…");
                Sys.RemoveFile(audio);
                try
                {
                    Tools.Run(job, AudioDownload(paths, referer, work, infoJson), "yt-dlp audio fallback");
                }
                catch (MimicException fallbackError)
                {
                    throw new MimicException(
                        $"Local video audio extraction failed ({error.Message}); online audio fallback failed ({fallbackError.Message}).");
                }
            }
        }
        else
        {
            report("Downloading the audio…");
            Tools.Run(job, AudioDownload(paths, referer, work, infoJson), "yt-dlp");
        }
        return RunWhisper(job, paths, audio, work, report);
    }

    private static string SafeTitleChars(string title)
    {
        var safe = new StringBuilder(title.Length);
        foreach (var c in title) safe.Append("<>:\"/\\|?*".Contains(c) || char.IsControl(c) ? '_' : c);
        return safe.ToString();
    }

    internal static string VideoDownloadStem(string title, string id)
    {
        var safeId = new string(id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-').ToArray());
        if (safeId.Length == 0) throw new MimicException("yt-dlp did not provide a usable video ID.");
        var safeTitle = Sys.TakeChars(SafeTitleChars(title).Trim().TrimEnd('.').Trim(), 120);
        if (safeTitle.Length == 0) safeTitle = "Video";
        return $"{safeTitle} [{safeId}]";
    }

    private static bool HasContent(string file)
    {
        try { return File.Exists(file) && Sys.FileLength(file) > 0; } catch { return false; }
    }

    private static string DownloadVideo(Job job, Paths paths, string infoJson, JsonElement info, string? referer, Action<string> report)
    {
        var id = Str(info, "id") is { } found && found.Trim().Length > 0 ? found : Sys.UniqueName();
        var title = Str(info, "title") ?? "Video";
        var stem = VideoDownloadStem(title, id);
        var downloads = paths.Downloads;
        Sys.Io(() => Directory.CreateDirectory(downloads), e => $"Could not create the install's downloads folder: {e}");
        var destination = Sys.Join(downloads, $"{stem}.mp4");
        if (File.Exists(destination))
        {
            if (HasContent(destination) && ProbeDuration(paths, destination, job) is not null)
            {
                report("Using the local video copy already in Stored files…");
                return destination;
            }
            Sys.Io(() => File.Delete(destination), e => $"Could not replace an incomplete video in Downloads: {e}");
        }

        report("Downloading a local video copy…");
        var staging = Sys.Join(downloads, $".staging-{Sys.UniqueName()}");
        Sys.Io(() => Directory.CreateDirectory(staging), e => $"Could not prepare the local video download: {e}");
        var stagedFile = Sys.Join(staging, $"{stem}.mp4");
        var outputTemplate = Sys.Join(staging, $"{stem}.%(ext)s");
        var command = YtDlp(paths, referer)
            .Args(
                "-f",
                "bv*[ext=mp4][vcodec^=avc1]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b",
                "--merge-output-format",
                "mp4",
                "--recode-video",
                "mp4",
                "--embed-metadata",
                "--windows-filenames",
                "--no-progress",
                "-o")
            .Args(outputTemplate)
            .Args("--load-info-json", infoJson);
        try
        {
            Tools.Run(job, command, "yt-dlp");
        }
        catch (MimicException)
        {
            Sys.RemoveDirAll(staging);
            throw;
        }
        if (!HasContent(stagedFile))
        {
            Sys.RemoveDirAll(staging);
            throw new MimicException("yt-dlp finished without creating the MP4 download.");
        }
        if (ProbeDuration(paths, stagedFile, job) is null)
        {
            Sys.RemoveDirAll(staging);
            throw new MimicException("The downloaded video could not be checked for playback.");
        }

        if (HasContent(destination))
        {
            Sys.RemoveFile(stagedFile);
        }
        else
        {
            if (File.Exists(destination) || Directory.Exists(destination)) Sys.RemoveFile(destination);
            try
            {
                File.Move(stagedFile, destination);
            }
            catch (Exception error)
            {
                Sys.RemoveDirAll(staging);
                throw new MimicException($"Could not move the downloaded video into Downloads: {Sys.IoMessage(error)}");
            }
        }
        Sys.RemoveDirAll(staging);
        return destination;
    }

    /// <summary>The whole job.</summary>
    public static Transcript Run(Job job, Paths paths, string url, string? referer, Action<string> report) =>
        TranscribeInner(job, paths, url, referer, report, false, false);

    /// <summary>
    /// A regular link can optionally save a local playback copy; audio transcription stays fast by default.
    /// </summary>
    internal static Transcript WithPlaybackCopy(Job job, Paths paths, string url, string? referer, bool saveVideo, Action<string> report) =>
        TranscribeInner(job, paths, url, referer, report, false, saveVideo);

    /// <summary>Embedded video tries require a local copy so the caller can hop to another known source.</summary>
    internal static Transcript EmbeddedLink(Job job, Paths paths, string url, string? referer, Action<string> report) =>
        TranscribeInner(job, paths, url, referer, report, true, true);

    private static void RequireTools(Paths paths)
    {
        var missing = paths.MissingTools();
        if (missing.Count > 0) throw new MimicException($"Missing tools: {string.Join(", ", missing)}.");
    }

    private static Transcript TranscribeInner(
        Job job, Paths paths, string rawUrl, string? referer, Action<string> report, bool requireLocalVideo, bool saveVideoForPlayback)
    {
        var url = ValidateUrl(rawUrl);
        RequireTools(paths);

        report("Reading the video's details…");
        var command = YtDlp(paths, referer)
            .Args(
                "--dump-single-json",
                "--skip-download",
                "--socket-timeout",
                "20",
                "--retries",
                "1",
                "--extractor-retries",
                "1",
                "--")
            .Args(url);
        var rawInfo = Tools.RunWithTimeout(job, command, "yt-dlp", null, TimeSpan.FromSeconds(60));
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawInfo);
        }
        catch (JsonException error)
        {
            throw new MimicException($"Could not read yt-dlp's video details: {error.Message}");
        }
        using var parsed = document;
        var info = parsed.RootElement;
        // A web page holding several videos comes back as a list.
        if (Str(info, "_type") == "playlist")
        {
            var first = Field(info, "entries") is { ValueKind: JsonValueKind.Array } entries && entries.GetArrayLength() > 0
                ? entries[0]
                : default;
            if (first.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                throw new MimicException($"{Tools.NotDownloadable}: the page holds no videos.");
            }
            info = first;
        }
        rawInfo = info.GetRawText();
        if (Field(info, "is_live") is { ValueKind: JsonValueKind.True })
        {
            throw new MimicException("This video is live right now. Try again after the stream ends.");
        }

        var stamp = Sys.Now();
        var work = Sys.Join(paths.WorkDir(), stamp.Nanos);
        Sys.Io(() => Directory.CreateDirectory(work));
        var infoJson = Sys.Join(work, "info.json");
        List<Segment> segments;
        string language;
        string? localVideoPath = null;
        string? downloadNotice = null;
        List<string> mediaHosts;
        long durationSeconds;
        try
        {
            // Reusing the details avoids asking the site for them a second time.
            Sys.Io(() => File.WriteAllText(infoJson, rawInfo, Sys.Utf8));
            mediaHosts = MetadataHosts(info);
            if (requireLocalVideo || saveVideoForPlayback)
            {
                var firstHost = mediaHosts.FirstOrDefault() ?? "video site";
                report($"Trying video extraction from {firstHost}…");
                try
                {
                    localVideoPath = DownloadVideo(job, paths, infoJson, info, referer, report);
                    report($"Video extraction succeeded from {firstHost}. The local copy is ready.");
                    downloadNotice = $"Video extraction succeeded from {firstHost}. Local playback is ready.";
                }
                catch (MimicException)
                {
                    if (job.Cancelled) throw new MimicException(Tools.Cancelled);
                    var hosts = mediaHosts.Count == 0 ? "the video site" : string.Join(", ", mediaHosts);
                    if (requireLocalVideo)
                    {
                        report($"Video extraction from {hosts} failed; trying the next available source.");
                        throw new MimicException($"{Tools.NotDownloadable}: video extraction failed at {hosts}.");
                    }
                    report($"Video extraction from {hosts} failed; continuing with audio transcription.");
                    // Tool output can contain signed links; keep it out of the UI and library.
                    downloadNotice = $"The transcript completed, but Mimic could not save a local video copy from {hosts}. Playback may need the original website.";
                }
            }
            if (requireLocalVideo && localVideoPath is null)
            {
                var host = Host(url, false) ?? "video site";
                report($"No downloadable video source was found at {host}; trying the next available source.");
                throw new MimicException($"{Tools.NotDownloadable}: no downloadable video copy was found at {host}.");
            }
            language = Str(info, "language") ?? "";
            (segments, var detected) = TranscribeWithWhisper(job, paths, infoJson, localVideoPath, referer, work, report);
            if (detected is not null) language = detected;
            if (segments.Count == 0) throw new MimicException("No speech was found in this video.");
            durationSeconds = Field(info, "duration") is { ValueKind: JsonValueKind.Number } length ? Whole(length.GetDouble()) : 0;
            if (durationSeconds <= 0 && localVideoPath is not null)
            {
                durationSeconds = ProbeDuration(paths, localVideoPath, job) ?? 0;
            }
        }
        finally
        {
            Sys.RemoveDirAll(work);
        }

        var source = (Str(info, "extractor_key") ?? "video").ToLowerInvariant();
        return new Transcript(
            new Video
            {
                Id = $"{source}-{Str(info, "id") ?? ""}",
                Url = Str(info, "webpage_url") ?? url,
                LocalVideoPath = localVideoPath,
                MediaHosts = mediaHosts,
                Title = Str(info, "title") ?? "Untitled",
                Channel = Str(info, "channel") ?? Str(info, "uploader") ?? "",
                DurationSeconds = durationSeconds,
                TranscodeSeconds = 0,
                Language = language,
                Method = "whisper",
                CreatedAt = stamp.Seconds,
            },
            segments,
            downloadNotice);
    }

    /// <summary>A float rounded half away from zero, as a whole number.</summary>
    private static long Whole(double value)
    {
        if (double.IsNaN(value)) return 0;
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded >= long.MaxValue) return long.MaxValue;
        if (rounded <= long.MinValue) return long.MinValue;
        return (long)rounded;
    }

    internal static List<string> MetadataHosts(JsonElement info)
    {
        var hosts = new List<string>();
        void Add(JsonElement holder)
        {
            foreach (var key in (string[])["url", "manifest_url"])
            {
                if (Str(holder, key) is { } raw && Host(raw, true) is { } host)
                {
                    host = host.ToLowerInvariant();
                    if (!hosts.Contains(host)) hosts.Add(host);
                }
            }
        }
        foreach (var key in (string[])["formats", "requested_formats"])
        {
            if (Field(info, key) is { ValueKind: JsonValueKind.Array } entries)
            {
                foreach (var format in entries.EnumerateArray()) Add(format);
            }
        }
        Add(info);
        return hosts;
    }

    private static ToolCommand Probe(Paths paths, string file) =>
        new ToolCommand(Sys.Join(paths.FfmpegDir(), "ffprobe.exe"))
            .Args("-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", file);

    private static double? Seconds(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;

    private static long? ProbeDuration(Paths paths, string file, Job job)
    {
        string output;
        try
        {
            output = Tools.Run(job, Probe(paths, file), "ffprobe");
        }
        catch (MimicException)
        {
            return null;
        }
        return Seconds(output) is { } seconds && double.IsFinite(seconds) && seconds > 0 ? Whole(seconds) : null;
    }

    /// <summary>Transcribes a video file that is already on disk without downloading it again.</summary>
    public static Transcript LocalFile(Job job, Paths paths, string path, Action<string> report)
    {
        var file = Sys.Io(() => Sys.Canonicalize(path), e => $"Could not open the dropped video: {e}");
        if (!File.Exists(file)) throw new MimicException("Drop a video file from this PC.");
        RequireTools(paths);
        var stamp = Sys.Now();
        var work = Sys.Join(paths.WorkDir(), stamp.Nanos);
        Sys.Io(() => Directory.CreateDirectory(work));
        var audio = Sys.Join(work, "audio.wav");
        List<Segment> segments;
        string? language;
        long durationSeconds;
        try
        {
            report("Preparing the dropped video…");
            var command = new ToolCommand(Sys.Join(paths.FfmpegDir(), "ffmpeg.exe"))
                .Args("-nostdin", "-y", "-i", file)
                .Args("-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", audio);
            Tools.Run(job, command, "ffmpeg");
            (segments, language) = RunWhisper(job, paths, audio, work, report);
            if (segments.Count == 0) throw new MimicException("No speech was found in this video.");
            double seconds = 0;
            try
            {
                seconds = Seconds(Tools.Run(job, Probe(paths, file), "ffprobe")) ?? 0;
            }
            catch (MimicException)
            {
            }
            durationSeconds = Whole(seconds);
        }
        finally
        {
            Sys.RemoveDirAll(work);
        }
        var title = Sys.FileStem(file) is { } stem ? Store.CleanLocalVideoTitle(stem) : "Local video";
        return new Transcript(
            new Video
            {
                Id = $"local-{stamp.Nanos}-{Sys.UniqueName()}",
                Url = file,
                LocalVideoPath = null,
                MediaHosts = [],
                Title = title,
                Channel = "Local video",
                DurationSeconds = durationSeconds,
                TranscodeSeconds = 0,
                Language = language ?? "",
                Method = "whisper",
                CreatedAt = stamp.Seconds,
            },
            segments,
            null);
    }

    /// <summary>YouTube changes often, so yt-dlp needs updating when downloads start failing.</summary>
    public static string UpdateYtDlp(Paths paths)
    {
        var output = Tools.Run(new Job(), new ToolCommand(paths.YtDlp()).Args("-U"), "yt-dlp");
        return (Sys.Lines(output).LastOrDefault() ?? "Done.").Trim();
    }

    internal static string ValidateUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal)) return url;
        throw new MimicException("Paste a full video link starting with https://");
    }

    /// <summary>A link that opens the video at a moment, where the site supports it.</summary>
    public static string LinkAt(string url, long ms)
    {
        if (url.Contains("youtube.com/", StringComparison.Ordinal) || url.Contains("youtu.be/", StringComparison.Ordinal))
        {
            var joiner = url.Contains('?') ? '&' : '?';
            return $"{url}{joiner}t={Math.Max(ms, 0) / 1000}s";
        }
        return url;
    }
}
