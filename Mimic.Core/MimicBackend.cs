// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.ComponentModel;
using System.Diagnostics;

namespace Mimic.Core;

/// <summary>
/// Everything the window can ask the backend to do.
/// Every failure is a <see cref="MimicException"/> whose message is the text to show.
/// </summary>
public sealed class MimicBackend : IDisposable
{
    private const string Busy = "This tab is already running a job.";

    /// <summary>The picture types the Image button accepts, for the window's file picker.</summary>
    public static IReadOnlyList<string> ImageTypes { get; } = ["png", "jpg", "jpeg", "webp", "gif"];

    private readonly Store store;
    private readonly object storeLock = new();
    /// <summary>One per tab, so the two tabs can run at the same time.</summary>
    private readonly Job[] jobs = [new Job(), new Job()];

    /// <summary>A job's progress: the tab's slot, then the message.</summary>
    public event Action<int, string>? Progress;

    /// <summary>A recording has begun in this slot, so the window can show Finish.</summary>
    public event Action<int>? CaptureStarted;

    public Paths Paths { get; }

    /// <summary>The app's version, as given at start.</summary>
    public string Version { get; }

    /// <summary>
    /// Opens the library in %USERPROFILE%\Mimic. <paramref name="installDir"/> is the folder the app runs from,
    /// where older versions kept downloaded videos.
    /// </summary>
    public MimicBackend(string installDir, string version)
        : this(
            Paths.FromEnv(),
            Sys.Join(installDir, "downloads"),
            Sys.Join(Sys.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "io.github.soybean-alien.mimic"), "downloads"),
            version)
    {
    }

    internal MimicBackend(Paths paths, string legacyDownloads, string downloads, string version)
    {
        Version = version;
        paths.Downloads = downloads;
        Sys.Io(() => Directory.CreateDirectory(paths.Root));
        Sys.Io(() => Directory.CreateDirectory(paths.LibraryRoot));
        Sys.Io(() => Directory.CreateDirectory(paths.Downloads));
        store = Store.Open(paths.Database());
        try
        {
            var (migrated, failed) = store.MigrateLegacyDownloads(legacyDownloads, paths.Downloads, paths.LegacyDownloadBackups());
            if (migrated > 0 || failed > 0)
            {
                Console.Error.WriteLine($"Mimic download migration: {migrated} copied, {failed} left at their previous paths.");
            }
        }
        catch
        {
            store.Dispose();
            throw;
        }
        // Clears anything a crash or forced close left behind.
        Sys.RemoveDirAll(paths.WorkDir());
        Paths = paths;
    }

    public void Dispose() => store.Dispose();

    private Job JobFor(int slot) => slot >= 0 && slot < jobs.Length ? jobs[slot] : throw new MimicException("Unknown tab.");

    private T WithStore<T>(Func<Store, T> work)
    {
        lock (storeLock) return work(store);
    }

    private void WithStore(Action<Store> work)
    {
        lock (storeLock) work(store);
    }

    /// <summary>Work off the window's thread, with anything unexpected turned into a message.</summary>
    private static Task<T> Background<T>(Func<T> work) => Task.Run(() =>
    {
        try
        {
            return work();
        }
        catch (MimicException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new MimicException(error.Message);
        }
    });

    /// <summary>Runs work in a tab's job slot, off the main thread.</summary>
    private Task<T> InSlot<T>(int slot, Func<Job, Action<string>, T> work) => Background(() =>
    {
        var job = JobFor(slot);
        if (!job.Start()) throw new MimicException(Busy);
        try
        {
            return work(job, message => Progress?.Invoke(slot, message));
        }
        finally
        {
            job.Finish();
        }
    });

    internal static TranscriptView View(Transcript transcript, IReadOnlyDictionary<long, string> titles)
    {
        var words = transcript.Segments.Sum(s => Sys.SplitWhitespace(s.Text).Length);
        var passages = Captions.GroupIntoPassages(transcript.Segments, Exports.PassageMs)
            .Select(p => new Passage(
                p.StartMs,
                Captions.Clock(p.StartMs),
                Transcribe.LinkAt(transcript.Video.Url, p.StartMs),
                titles.TryGetValue(p.StartMs, out var title) ? title : "",
                p.Text))
            .ToList();
        return new TranscriptView { Video = transcript.Video, Passages = passages, Segments = transcript.Segments, Words = words };
    }

    private TranscriptView View(Transcript transcript)
    {
        var id = transcript.Video.Id;
        var (titles, score, takeaways) = WithStore(s => (s.Titles(id), s.Score(id), s.Takeaways(id)));
        return View(transcript, titles) with { Score = score, Takeaways = takeaways };
    }

    /// <summary>Writes and saves section titles, one per passage.</summary>
    private void AddTitles(Job job, Transcript transcript, Action<string> report)
    {
        report("Titling each section…");
        var passages = Captions.GroupIntoPassages(transcript.Segments, Exports.PassageMs);
        var choice = WithStore(s => s.GetModelChoice());
        var titles = Ai.SectionTitles(job, Paths, choice, passages, report);
        var keyed = passages.Zip(titles, (p, title) => (p.StartMs, title)).Where(entry => entry.title.Length > 0).ToList();
        WithStore(s => s.SaveTitles(transcript.Video.Id, keyed));
    }

    private TranscriptView SaveAndTitle(Job job, Transcript transcript, Action<string> report)
    {
        WithStore(s => s.Save(transcript));
        var notices = new List<string>();
        if (transcript.Notice is not null) notices.Add(transcript.Notice);
        try
        {
            AddTitles(job, transcript, report);
        }
        catch (MimicException error)
        {
            notices.Add($"The transcript was saved, but its section titles failed. {error.Message}");
        }
        return View(transcript) with { Notice = notices.Count > 0 ? string.Join(" ", notices) : null };
    }

    private static Transcript Timed(Transcript transcript, Stopwatch started) =>
        transcript with { Video = transcript.Video with { TranscodeSeconds = (long)started.Elapsed.TotalSeconds } };

    /// <summary>missing_tools: names of required tools that are not on disk.</summary>
    public IReadOnlyList<string> MissingTools() => Paths.MissingTools();

    /// <summary>list_videos</summary>
    public IReadOnlyList<Video> ListVideos() => WithStore(s => s.List());

    /// <summary>get_library_path</summary>
    public string GetLibraryPath() => Paths.Database();

    /// <summary>open_library_folder</summary>
    public void OpenLibraryFolder() => Explorer(Paths.LibraryRoot, "Could not open the library folder");

    /// <summary>
    /// Keeps the library in <paramref name="folder"/> from the next start. A folder that has no library yet
    /// gets a copy of the current one; pictures already saved stay where they are and keep working.
    /// </summary>
    public void SetLibraryFolder(string folder)
    {
        var target = Sys.Join(folder, "library.db");
        if (!File.Exists(target)) WithStore(s => { s.CopyTo(target); return 0; });
        Sys.Io(() => File.WriteAllText(Paths.LibraryPointer(), folder), e => $"Could not remember the library folder: {e}");
    }

    /// <summary>open_stored_files</summary>
    public void OpenStoredFiles()
    {
        Sys.Io(() => Directory.CreateDirectory(Paths.Downloads), e => $"Could not open Stored files: {e}");
        Explorer(Paths.Downloads, "Could not open Stored files");
    }

    private static void Explorer(string folder, string failure)
    {
        try
        {
            var start = new ProcessStartInfo("explorer") { UseShellExecute = false };
            start.ArgumentList.Add(folder);
            Process.Start(start)?.Dispose();
        }
        catch (Exception error)
        {
            var text = error is Win32Exception { NativeErrorCode: 2 or 3 } ? "program not found" : Sys.IoMessage(error);
            throw new MimicException($"{failure}: {text}");
        }
    }

    /// <summary>
    /// get_video_playback_source. A local file's path comes back in the \\?\ form the library stores;
    /// <see cref="PlainPath"/> removes that prefix.
    /// </summary>
    public PlaybackSource GetVideoPlaybackSource(string id)
    {
        var video = WithStore(s => s.Get(id)).Video;
        if (video.Method == Exports.ImageMethod) throw new MimicException("Images do not have a video to play.");
        if (video.LocalVideoPath is not null)
        {
            string? path = null;
            try { path = Sys.Canonicalize(video.LocalVideoPath); } catch (Exception error) when (error is Win32Exception or IOException or ArgumentException or NotSupportedException) { }
            if (path is not null && File.Exists(path)) return new PlaybackSource(path, true);
        }
        if (video.Url.StartsWith("https://", StringComparison.Ordinal) || video.Url.StartsWith("http://", StringComparison.Ordinal))
        {
            return new PlaybackSource(video.Url, false);
        }
        var original = Sys.Io(() => Sys.Canonicalize(video.Url), e => $"The original local video is unavailable: {e}");
        if (!File.Exists(original)) throw new MimicException("The original local video is no longer available at its saved path.");
        return new PlaybackSource(original, true);
    }

    /// <summary>A path without the \\?\ prefix, for a player or picker that does not accept it.</summary>
    public static string PlainPath(string path) => Sys.StripVerbatim(path);

    /// <summary>summarize_video</summary>
    public Task<TranscriptView> SummarizeVideoAsync(int slot, string id) => InSlot(slot, (job, report) =>
    {
        var transcript = WithStore(s => s.Get(id));
        var choice = WithStore(s => s.GetModelChoice());
        var takeaways = Ai.SummarizeTranscript(job, Paths, choice, transcript.Segments, report);
        WithStore(s => s.SaveTakeaways(id, takeaways));
        return View(transcript);
    });

    /// <summary>get_transcript</summary>
    public TranscriptView GetTranscript(string id) => View(WithStore(s => s.Get(id)));

    /// <summary>update_video_title</summary>
    public TranscriptView UpdateVideoTitle(string id, string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) throw new MimicException("A title cannot be empty.");
        var transcript = WithStore(s =>
        {
            s.UpdateTitle(id, trimmed);
            return s.Get(id);
        });
        return View(transcript);
    }

    /// <summary>delete_video</summary>
    public void DeleteVideo(string id)
    {
        var transcript = WithStore(s => s.Get(id));
        WithStore(s => s.Delete(id));
        if (transcript.Video.Method == Exports.ImageMethod) Sys.RemoveFile(transcript.Video.Url);
    }

    /// <summary>cancel_job</summary>
    public void CancelJob(int slot) => JobFor(slot).RequestCancel();

    /// <summary>transcribe_video</summary>
    public Task<TranscriptView> TranscribeVideoAsync(int slot, string url, bool saveVideo = false) => InSlot(slot, (job, report) =>
    {
        var started = Stopwatch.StartNew();
        var transcript = Transcribe.WithPlaybackCopy(job, Paths, url, null, saveVideo, report);
        return SaveAndTitle(job, Timed(transcript, started), report);
    });

    /// <summary>transcribe_file</summary>
    public Task<TranscriptView> TranscribeFileAsync(int slot, string path) => InSlot(slot, (job, report) =>
    {
        var started = Stopwatch.StartNew();
        var transcript = Transcribe.LocalFile(job, Paths, path, report);
        return SaveAndTitle(job, Timed(transcript, started), report);
    });

    /// <summary>
    /// pick_embedded: shows the crosshair. Returns nothing if the user pressed Esc.
    /// The window minimizes itself before calling this and comes back after.
    /// </summary>
    public Task<Target?> PickEmbeddedAsync() => Background(Embedded.Pick);

    /// <summary>transcribe_embedded. <see cref="CaptureStarted"/> fires if it has to listen instead of download.</summary>
    public Task<TranscriptView> TranscribeEmbeddedAsync(int slot, Target target) => InSlot(slot, (job, report) =>
    {
        var started = Stopwatch.StartNew();
        var transcript = Embedded.Transcribe(job, Paths, target, report, () => CaptureStarted?.Invoke(slot));
        return SaveAndTitle(job, Timed(transcript, started), report);
    });

    /// <summary>finish_capture</summary>
    public void FinishCapture(int slot) => JobFor(slot).RequestFinishCapture();

    /// <summary>title_video: adds section titles to a transcript saved without them.</summary>
    public Task<TranscriptView> TitleVideoAsync(int slot, string id) => InSlot(slot, (job, report) =>
    {
        var transcript = WithStore(s => s.Get(id));
        AddTitles(job, transcript, report);
        return View(transcript);
    });

    /// <summary>describe_image</summary>
    public Task<TranscriptView> DescribeImageAsync(int slot, string path) => InSlot(slot, (job, report) =>
    {
        report("Writing the reverse prompt and scoring the image…");
        var choice = WithStore(s => s.GetModelChoice());
        var scoring = Task.Run(() =>
        {
            try
            {
                return (Score: (ImageScore?)Ai.AiScore(job, Paths, choice, path), Error: (string?)null);
            }
            catch (MimicException error)
            {
                return (null, error.Message);
            }
            catch
            {
                return (null, "The scoring stopped unexpectedly.");
            }
        });
        string text;
        try
        {
            text = Ai.ReversePrompt(job, Paths, choice, path);
        }
        finally
        {
            scoring.Wait();
        }
        var (score, scoreError) = scoring.Result;

        // The library keeps its own copy, so the entry survives the original moving.
        var stamp = Sys.Now();
        var id = $"image-{stamp.Nanos}";
        var extension = Sys.Extension(path) ?? "png";
        var images = Paths.ImagesDir();
        Sys.Io(() => Directory.CreateDirectory(images));
        var copy = Sys.Join(images, $"{id}.{extension}");
        Sys.Io(() => File.Copy(path, copy, true));

        var transcript = new Transcript(
            new Video
            {
                Id = id,
                Url = copy,
                LocalVideoPath = null,
                MediaHosts = [],
                Title = Sys.FileName(path) ?? id,
                Channel = "",
                DurationSeconds = 0,
                TranscodeSeconds = 0,
                Language = "",
                Method = Exports.ImageMethod,
                CreatedAt = stamp.Seconds,
            },
            [new Segment(0, 0, text)],
            null);
        WithStore(s => s.Save(transcript));
        string? notice = null;
        if (score is not null) WithStore(s => s.SaveScore(id, score));
        else notice = $"Reverse prompt saved, but the AI score failed. {scoreError}";
        return View(transcript) with { Notice = notice };
    });

    /// <summary>The saved picture's file, for a window that shows pictures from disk.</summary>
    public string ImagePath(string id) => WithStore(s => s.Get(id)).Video.Url;

    /// <summary>image_data: a saved picture as a data URL.</summary>
    public string ImageData(string id) => Ai.DataUrl(ImagePath(id));

    /// <summary>get_model</summary>
    public ModelChoice GetModel() => WithStore(s => s.GetModelChoice());

    /// <summary>set_model</summary>
    public void SetModel(ModelChoice choice)
    {
        choice.Check();
        WithStore(s => s.SaveModelChoice(choice));
    }

    /// <summary>openai_status</summary>
    public Task<string> OpenAIStatusAsync() => Background(() => Ai.OpenAIStatus(Paths));

    /// <summary>openai_sign_in</summary>
    public Task<string> OpenAISignInAsync() => Background(() => Ai.OpenAISignIn(Paths));

    /// <summary>update_yt_dlp</summary>
    public Task<string> UpdateYtDlpAsync() => Background(() => Transcribe.UpdateYtDlp(Paths));

    /// <summary>The name the save dialog starts with: the entry's title made safe, plus the extension.</summary>
    public string SuggestedFileName(string id, string extension) =>
        $"{Exports.SafeFileName(WithStore(s => s.Get(id)).Video.Title)}.{extension}";

    /// <summary>
    /// export_transcript. "srt" and "txt" are written to <paramref name="outputPath"/>, the file the user chose;
    /// pass null when they cancelled, and null comes back. "md" is written there too when a path is given; with
    /// null it files the note in the Mimic Obsidian project folder, failing with OBSIDIAN_CONFIRMATION_REQUIRED
    /// until <paramref name="allowObsidianVault"/> is true. Returns the path written.
    /// </summary>
    public Task<string?> ExportTranscriptAsync(string id, string format, bool allowObsidianVault, string? outputPath) => Background(() =>
    {
        var (transcript, titles, score, takeaways) = WithStore(s => (s.Get(id), s.Titles(id), s.Score(id), s.Takeaways(id)));
        if (format is not ("srt" or "txt" or "md")) throw new MimicException($"Unknown format: {format}");
        string path;
        if (format == "md" && outputPath is not null)
        {
            path = outputPath;
        }
        else if (format == "md")
        {
            var vaultRoot = Paths.ObsidianVaultRoot();
            if (Directory.Exists(Sys.Join(vaultRoot, ".obsidian")) && !allowObsidianVault)
            {
                throw new MimicException("OBSIDIAN_CONFIRMATION_REQUIRED");
            }
            path = Exports.UniqueVaultNotePath(Paths, transcript.Video.Title);
        }
        else
        {
            if (outputPath is null) return null;
            path = outputPath;
        }
        var body = format switch
        {
            "srt" => Captions.ToSrt(transcript.Segments),
            "txt" => Exports.PlainText(transcript, titles),
            _ => Exports.ToMarkdown(transcript, titles, score, Exports.CopyPictureBeside(transcript, path), takeaways),
        };
        Sys.Io(() => File.WriteAllText(path, body, Sys.Utf8));
        return (string?)path;
    });

    /// <summary>
    /// export_pdf: prints to Microsoft Print to PDF without further prompts. <paramref name="outputPath"/> is the
    /// file the user chose; pass null when they cancelled, and false comes back.
    /// </summary>
    public Task<bool> ExportPdfAsync(string id, string? outputPath) => Background(() =>
    {
        var (transcript, titles, score, takeaways) = WithStore(s => (s.Get(id), s.Titles(id), s.Score(id), s.Takeaways(id)));
        if (outputPath is null) return false;
        Exports.PrintPdf(Paths, transcript, titles, score, takeaways, outputPath);
        return true;
    });

    /// <summary>open_link</summary>
    public void OpenLink(string url)
    {
        if (!url.StartsWith("https://", StringComparison.Ordinal) && !url.StartsWith("http://", StringComparison.Ordinal))
        {
            throw new MimicException("Only web links can be opened.");
        }
        Sys.Io(() => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose());
    }

    /// <summary>mimic_version</summary>
    public string MimicVersion() => Version;
}
