// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Diagnostics;
using Mimic.Core;

namespace Mimic.Tests;

/// <summary>
/// Runs the installed tools for real, from a library in TEMP that only links to the real tools and model
/// folders. Nothing is written to the real library. Set MIMIC_LIVE_TESTS=1 to run them.
/// </summary>
public class LiveToolTests
{
    private sealed class LinkedLibrary : IDisposable
    {
        private readonly TempFolder dir = new();
        public string Folder => dir.Path;
        public Paths Paths { get; }

        public LinkedLibrary()
        {
            var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Mimic");
            var root = Path.Combine(dir.Path, "Mimic");
            Directory.CreateDirectory(root);
            foreach (var name in (string[])["tools", "models"])
            {
                Tools.Run(new Job(), new ToolCommand("cmd").Args("/c", "mklink", "/J", Path.Combine(root, name), Path.Combine(real, name)), "mklink");
            }
            Paths = new Paths(root) { Downloads = Path.Combine(dir.Path, "downloads") };
            Directory.CreateDirectory(Paths.Downloads);
        }

        public void Dispose()
        {
            // Remove the links themselves first, so nothing can follow them into the real folders.
            foreach (var name in (string[])["tools", "models"]) Directory.Delete(Path.Combine(Paths.Root, name), false);
            dir.Dispose();
        }
    }

    [LiveFact]
    public void A_spoken_file_is_transcribed_by_the_real_ffmpeg_and_whisper()
    {
        using var library = new LinkedLibrary();
        Assert.Empty(library.Paths.MissingTools());
        var wav = Path.Combine(library.Folder, "Spoken test.wav");
        var speak = "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
            + $"$s.SetOutputToWaveFile('{wav}'); $s.Speak('The quick brown fox jumps over the lazy dog. This is a test of the transcription.'); $s.Dispose()";
        Tools.Run(new Job(), Tools.EncodedPowershell(speak), "Speech");

        var messages = new List<string>();
        var transcript = Transcribe.LocalFile(new Job(), library.Paths, wav, messages.Add);

        var text = string.Join(" ", transcript.Segments.Select(s => s.Text)).ToLowerInvariant();
        Assert.Contains("quick brown fox", text, StringComparison.Ordinal);
        Assert.Equal("whisper", transcript.Video.Method);
        Assert.Equal("Spoken test", transcript.Video.Title);
        Assert.NotEmpty(messages);
        Assert.False(Directory.Exists(library.Paths.WorkDir()) && Directory.EnumerateFileSystemEntries(library.Paths.WorkDir()).Any());
        Console.WriteLine($"local file: {transcript.Video.Url} | {transcript.Video.LocalVideoPath} | {transcript.Video.DurationSeconds}s | {text}");
        Console.WriteLine(string.Join(" / ", messages));
    }

    [LiveFact]
    public void A_pdf_is_printed_without_prompts()
    {
        using var library = new LinkedLibrary();
        var output = Path.Combine(library.Folder, "out.pdf");
        var transcript = StoreTests.Transcript("a", "Words to print.");
        var started = Stopwatch.StartNew();
        Exports.PrintPdf(library.Paths, transcript, new Dictionary<long, string> { [0] = "Opening" }, null, "## Key takeaways\n- One", output);
        Assert.Equal("%PDF"u8.ToArray(), File.ReadAllBytes(output)[..4]);
        Assert.Empty(Directory.EnumerateFiles(library.Paths.WorkDir()));
        Console.WriteLine($"pdf: {new FileInfo(output).Length} bytes in {started.Elapsed.TotalSeconds:0.0}s");
    }

    [LiveFact]
    public void A_short_public_video_is_transcribed_from_its_link()
    {
        using var library = new LinkedLibrary();
        var messages = new List<string>();
        var transcript = Transcribe.Run(new Job(), library.Paths, "https://www.youtube.com/watch?v=jNQXAC9IVRw", null, messages.Add);
        Assert.NotEmpty(transcript.Segments);
        Assert.Equal("youtube-jNQXAC9IVRw", transcript.Video.Id);
        Console.WriteLine($"link: {transcript.Video.Title} | {transcript.Video.Channel} | {transcript.Video.Method} | {transcript.Video.DurationSeconds}s | hosts {string.Join(",", transcript.Video.MediaHosts)} | notice {transcript.Notice}");
        Console.WriteLine(string.Join(" / ", messages));
        Console.WriteLine(string.Join(" ", transcript.Segments.Select(s => s.Text)));
    }
}
