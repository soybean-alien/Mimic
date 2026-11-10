// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
namespace Mimic.Core;

/// <summary>Where Mimic keeps its library, tools and models.</summary>
public sealed class Paths
{
    private const string FastModelFile = "ggml-large-v3-turbo.bin";
    internal const string BundledModelFile = "ggml-large-v3-turbo-q5_0.bin";

    public string Root { get; }
    public string Downloads { get; set; }
    /// <summary>Where the library database and its pictures are kept. The user can move it; tools and models stay under Root.</summary>
    public string LibraryRoot { get; set; }

    public Paths(string root)
    {
        Root = root;
        LibraryRoot = root;
        Downloads = Sys.Join(root, "downloads");
    }

    public static Paths FromEnv()
    {
        // Not AppData: packaged apps get a private redirected copy of it, so tools
        // installed from one would be invisible to the other.
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(home)) throw new MimicException("USERPROFILE is not set.");
        var paths = new Paths(Sys.Join(home, "Mimic"));
        try
        {
            var chosen = File.ReadAllText(paths.LibraryPointer()).Trim();
            if (chosen.Length > 0 && Directory.Exists(chosen)) paths.LibraryRoot = chosen;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return paths;
    }

    public string YtDlp() => Sys.Join(Root, "tools/yt-dlp/yt-dlp.exe");
    public string FfmpegDir() => Sys.Join(Root, "tools/ffmpeg");
    public string Whisper() => Sys.Join(Root, "tools/whisper/Release/whisper-cli.exe");

    public string Model()
    {
        var models = Sys.Join(Root, "models");
        var full = Sys.Join(models, FastModelFile);
        return File.Exists(full) ? full : Sys.Join(models, BundledModelFile);
    }

    public string Database() => Sys.Join(LibraryRoot, "library.db");

    /// <summary>A one-line file naming the library folder the user chose, if any.</summary>
    public string LibraryPointer() => Sys.Join(Root, "library-folder.txt");

    public string LegacyDownloadBackups() => Sys.Join(Sys.ParentOrSelf(Downloads), "legacy-download-backups");

    public string ObsidianVaultRoot()
    {
        var vault = Environment.GetEnvironmentVariable("MIMIC_OBSIDIAN_VAULT");
        if (string.IsNullOrEmpty(vault))
        {
            throw new MimicException("Set MIMIC_OBSIDIAN_VAULT to your Obsidian vault folder before saving Markdown.");
        }
        if (!Directory.Exists(vault)) throw new MimicException("The configured Obsidian vault folder does not exist.");
        return vault;
    }

    public string VaultProjectsDir() => Sys.Join(ObsidianVaultRoot(), "Claude Code/Development/Projects/Mimic/Projects");

    public string WorkDir() => Sys.Join(Root, "work");
    public string ImagesDir() => Sys.Join(LibraryRoot, "images");

    public string Claude() => Sys.Join(Sys.ParentOrSelf(Root), ".local/bin/claude.exe");

    public string Codex()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (var directory in path.Split(';'))
            {
                var candidate = Sys.Join(directory.Trim('"'), "codex.exe");
                if (File.Exists(candidate)) candidates.Add(candidate);
            }
        }
        var home = Sys.ParentOrSelf(Root);
        var versionedBins = new List<string> { Sys.Join(home, "AppData/Local/OpenAI/Codex/bin") };
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(localAppData))
        {
            var bin = Sys.Join(localAppData, "OpenAI/Codex/bin");
            if (!Sys.Components(bin).SequenceEqual(Sys.Components(versionedBins[0]))) versionedBins.Add(bin);
        }
        foreach (var versionedBin in versionedBins)
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(versionedBin))
                {
                    var candidate = Sys.Join(entry, "codex.exe");
                    if (File.Exists(candidate)) candidates.Add(candidate);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (candidates.Count == 0)
        {
            throw new MimicException(
                $"Codex CLI was not found on PATH or in {versionedBins[0]}. Install or update the Codex desktop CLI, then restart Mimic.");
        }
        return candidates
            .Select(candidate => (candidate, modified: Modified(candidate)))
            .OrderBy(entry => entry.modified)
            .Last()
            .candidate;
    }

    private static DateTime Modified(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.UnixEpoch; }
    }

    /// <summary>Names of required tools that are not on disk.</summary>
    public List<string> MissingTools()
    {
        (string Name, string Path)[] checks =
        [
            ("yt-dlp", YtDlp()),
            ("ffmpeg", Sys.Join(FfmpegDir(), "ffmpeg.exe")),
            ("Whisper", Whisper()),
            ("Whisper model", Model()),
        ];
        return checks.Where(check => !File.Exists(check.Path) && !Directory.Exists(check.Path)).Select(check => check.Name).ToList();
    }
}
