// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mimic.Core;

/// <summary>Downloads the tools Mimic needs into its folder, so setup needs no installer script.</summary>
public sealed class ToolInstaller : IDisposable
{
    public const string YtDlp = "yt-dlp";
    public const string Ffmpeg = "ffmpeg";
    public const string Whisper = "Whisper";
    public const string WhisperModel = "Whisper model";

    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    /// <summary>GitHub's own record of each release, which carries the SHA-256 of every file in it.</summary>
    internal const string YtDlpReleaseUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    internal const string YtDlpAsset = "yt-dlp.exe";
    internal const string FfmpegReleaseUrl = "https://api.github.com/repos/yt-dlp/FFmpeg-Builds/releases/tags/latest";
    internal const string FfmpegAsset = "ffmpeg-master-latest-win64-gpl.zip";
    /// <summary>whisper.cpp publishes its Windows builds on some releases only, so the newest one that has it is used.</summary>
    internal const string WhisperReleasesUrl = "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=30";
    internal const string WhisperAsset = "whisper-bin-x64.zip";
    public const string WhisperFallbackUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
    internal const string WhisperFallbackSha256 = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb";
    public const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin";
    internal const string ModelSha1 = "e050f7970618a659205450ad97eb95a18d69c9ee";

    private readonly Paths paths;
    private readonly HttpClient http;

    /// <summary>Installs under <paramref name="root"/>, which is %USERPROFILE%\Mimic for the real app.</summary>
    public ToolInstaller(string root) : this(root, new HttpClientHandler())
    {
    }

    public ToolInstaller(Paths paths) : this(paths.Root)
    {
    }

    internal ToolInstaller(string root, HttpMessageHandler handler)
    {
        paths = new Paths(root);
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mimic");
    }

    public void Dispose() => http.Dispose();

    /// <summary>The tools <see cref="InstallAsync"/> knows, in the order Mimic lists them.</summary>
    public static IReadOnlyList<string> ToolNames { get; } = [YtDlp, Ffmpeg, Whisper, WhisperModel];

    /// <summary>
    /// Downloads and installs one tool: "yt-dlp", "ffmpeg", "Whisper" or "Whisper model".
    /// Failures are a <see cref="MimicException"/>; cancelling throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task InstallAsync(string tool, IProgress<(string message, double? fraction)>? progress, CancellationToken ct)
    {
        try
        {
            switch (tool)
            {
                case YtDlp:
                    await InstallYtDlp(progress, ct).ConfigureAwait(false);
                    break;
                case Ffmpeg:
                    await InstallFfmpeg(progress, ct).ConfigureAwait(false);
                    break;
                case Whisper:
                    await InstallWhisper(progress, ct).ConfigureAwait(false);
                    break;
                case WhisperModel:
                    await InstallModel(progress, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new MimicException($"Unknown tool: {tool}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (MimicException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new MimicException($"Could not install {tool}: {(error is HttpRequestException or InvalidDataException or OperationCanceledException ? error.Message : Sys.IoMessage(error))}");
        }
        progress?.Report(($"{tool} is installed.", 1));
    }

    /// <summary>Installs every missing tool except the Whisper model, which is large and installed on its own.</summary>
    public async Task InstallMissingToolsAsync(IProgress<(string message, double? fraction)>? progress, CancellationToken ct)
    {
        foreach (var tool in paths.MissingTools().Where(tool => tool != WhisperModel))
        {
            await InstallAsync(tool, progress, ct).ConfigureAwait(false);
        }
    }

    private async Task InstallYtDlp(IProgress<(string, double?)>? progress, CancellationToken ct)
    {
        var target = Path.GetFullPath(paths.YtDlp());
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        var build = await PublishedBuild(YtDlpReleaseUrl, YtDlpAsset, YtDlp, ct).ConfigureAwait(false);
        await DownloadChecked(build, part, YtDlp, progress, ct).ConfigureAwait(false);
        File.Move(part, target, true);
    }

    private async Task InstallFfmpeg(IProgress<(string, double?)>? progress, CancellationToken ct)
    {
        var folder = Path.GetFullPath(paths.FfmpegDir());
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, "ffmpeg.zip.part");
        var build = await PublishedBuild(FfmpegReleaseUrl, FfmpegAsset, Ffmpeg, ct).ConfigureAwait(false);
        await DownloadChecked(build, zip, Ffmpeg, progress, ct).ConfigureAwait(false);
        try
        {
            progress?.Report(("Unpacking ffmpeg…", null));
            using var archive = ZipFile.OpenRead(zip);
            foreach (var name in (string[])["ffmpeg.exe", "ffprobe.exe"])
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase) || e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new MimicException($"The ffmpeg download did not contain {name}.");
                Unpack(entry, Path.Combine(folder, name));
            }
        }
        finally
        {
            Sys.RemoveFile(zip);
        }
    }

    private async Task InstallWhisper(IProgress<(string, double?)>? progress, CancellationToken ct)
    {
        var folder = Path.GetFullPath(Sys.ParentOrSelf(paths.Whisper()));
        Directory.CreateDirectory(folder);
        progress?.Report(("Finding the newest Whisper build…", null));
        var build = await FindBuild(WhisperReleasesUrl, WhisperAsset, ct).ConfigureAwait(false) ?? (WhisperFallbackUrl, WhisperFallbackSha256);
        var zip = Path.Combine(Path.GetDirectoryName(folder)!, "whisper.zip.part");
        await DownloadChecked(build, zip, Whisper, progress, ct).ConfigureAwait(false);
        try
        {
            progress?.Report(("Unpacking Whisper…", null));
            using var archive = ZipFile.OpenRead(zip);
            var found = false;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.FullName.StartsWith("Release/", StringComparison.OrdinalIgnoreCase) || entry.Name.Length == 0) continue;
                var target = Path.GetFullPath(Path.Combine(folder, entry.FullName["Release/".Length..]));
                if (!target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Unpack(entry, target);
                found |= entry.Name.Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase);
            }
            if (!found) throw new MimicException("The Whisper download did not contain whisper-cli.exe.");
        }
        finally
        {
            Sys.RemoveFile(zip);
        }
    }

    /// <summary>The file's address and SHA-256 from GitHub's release record; without a checksum nothing is downloaded.</summary>
    private async Task<(string Url, string Sha256)> PublishedBuild(string releaseUrl, string assetName, string tool, CancellationToken ct) =>
        await FindBuild(releaseUrl, assetName, ct).ConfigureAwait(false)
        ?? throw new MimicException($"Could not install {tool}: GitHub did not give the checksum for its download, so nothing was downloaded. Try again later.");

    /// <summary>Reads one release, or a list of them newest first, and returns the first file of that name that has a SHA-256.</summary>
    private async Task<(string Url, string Sha256)?> FindBuild(string releaseUrl, string assetName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var reply = await http.GetAsync(releaseUrl, ct).ConfigureAwait(false);
            reply.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = json.RootElement;
            foreach (var release in root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root])
            {
                if (!release.TryGetProperty("assets", out var assets)) continue;
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() != assetName) continue;
                    if (!asset.TryGetProperty("digest", out var digest) || digest.GetString() is not { } text) continue;
                    if (!text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || text.Length != 71) continue;
                    return (asset.GetProperty("browser_download_url").GetString()!, text[7..].ToLowerInvariant());
                }
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
        }
        return null;
    }

    /// <summary>Downloads a program file and deletes it unless its SHA-256 is the published one.</summary>
    private async Task DownloadChecked((string Url, string Sha256) build, string part, string tool, IProgress<(string, double?)>? progress, CancellationToken ct)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await Download(build.Url, part, tool, sha256, progress, ct).ConfigureAwait(false);
        if (Convert.ToHexStringLower(sha256.GetHashAndReset()) == build.Sha256) return;
        Sys.RemoveFile(part);
        throw new MimicException($"The {tool} download did not match its published checksum, so it was deleted. Try again.");
    }

    private async Task InstallModel(IProgress<(string, double?)>? progress, CancellationToken ct)
    {
        var target = Path.GetFullPath(Sys.Join(Sys.Join(paths.Root, "models"), Paths.BundledModelFile));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await Download(ModelUrl, part, WhisperModel, sha1, progress, ct).ConfigureAwait(false);
        var actual = Convert.ToHexStringLower(sha1.GetHashAndReset());
        if (actual != ModelSha1)
        {
            Sys.RemoveFile(part);
            throw new MimicException("The Whisper model download was damaged (its checksum did not match), so it was deleted. Try again.");
        }
        File.Move(part, target, true);
    }

    private static void Unpack(ZipArchiveEntry entry, string target)
    {
        var part = target + ".part";
        try
        {
            entry.ExtractToFile(part, true);
            File.Move(part, target, true);
        }
        catch
        {
            Sys.RemoveFile(part);
            throw;
        }
    }

    /// <summary>Downloads to a file, reporting bytes as they arrive. A failed or cancelled download leaves nothing behind.</summary>
    private async Task Download(string url, string part, string tool, IncrementalHash? hash, IProgress<(string, double?)>? progress, CancellationToken ct)
    {
        progress?.Report(($"Downloading {tool}…", null));
        try
        {
            using var reply = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            reply.EnsureSuccessStatusCode();
            var total = reply.Content.Headers.ContentLength;
            await using var source = await reply.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buffer = new byte[1 << 16];
            long done = 0;
            long reported = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash?.AppendData(buffer, 0, read);
                done += read;
                if (done - reported < 1 << 20 && done != total) continue;
                reported = done;
                progress?.Report(total is > 0
                    ? ($"Downloading {tool}: {Megabytes(done)} of {Megabytes(total.Value)} MB", (double)done / total.Value)
                    : ($"Downloading {tool}: {Megabytes(done)} MB", null));
            }
            if (total is { } expected && done != expected) throw new MimicException($"The {tool} download stopped early. Try again.");
        }
        catch
        {
            Sys.RemoveFile(part);
            throw;
        }
    }

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
