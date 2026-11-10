// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Text.Json;

namespace Mimic.Core;

/// <summary>The Embedded button: point at a video on screen and transcribe it, link or no link.</summary>
public static class Embedded
{
    /// <summary>Shows the crosshair. Returns what was clicked, or nothing if the user pressed Esc.</summary>
    public static Target? Pick()
    {
        var reply = Tools.Run(new Job(), Tools.EncodedPowershell(Tools.Script("pick_video.ps1")), "The crosshair").Trim();
        if (reply == "{}") return null;
        try
        {
            return Target.FromJson(reply);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            throw new MimicException($"Could not read what was clicked: {error.Message}");
        }
    }

    /// <summary>Downloads the video if any address leads to one. Otherwise records its sound as it plays.</summary>
    public static Transcript Transcribe(Job job, Paths paths, Target target, Action<string> report, Action onCapture)
    {
        foreach (var link in target.Links())
        {
            var host = Core.Transcribe.Host(link, false) ?? "video site";
            report($"Trying an available video source at {host}…");
            try
            {
                var transcript = Core.Transcribe.EmbeddedLink(job, paths, link, target.Page(), report);
                report($"Video extraction succeeded from {transcript.Video.MediaHosts.FirstOrDefault() ?? host}.");
                return transcript;
            }
            catch (MimicException error) when (error.Message.StartsWith(Tools.NotDownloadable, StringComparison.Ordinal))
            {
                report($"Video extraction from {host} failed; trying the next available source.");
            }
        }

        var stamp = Sys.Now();
        var work = Sys.Join(paths.WorkDir(), stamp.Nanos);
        Sys.Io(() => Directory.CreateDirectory(work));
        List<Segment> segments;
        string? language;
        try
        {
            onCapture();
            report("This one can't be downloaded, so Mimic is listening instead. Play the video, then press Finish when it ends.");
            var audio = Sys.Join(work, "audio.wav");
            Capture.Record(job, target.ProcessId, audio);
            (segments, language) = Core.Transcribe.RunWhisper(job, paths, audio, work, report);
            if (segments.Count == 0) throw new MimicException("No speech was found in the recording.");
        }
        finally
        {
            Sys.RemoveDirAll(work);
        }

        return new Transcript(
            new Video
            {
                Id = $"capture-{stamp.Nanos}",
                Url = target.Page() ?? "",
                LocalVideoPath = null,
                MediaHosts = [],
                Title = target.Title,
                Channel = "",
                DurationSeconds = 0,
                TranscodeSeconds = 0,
                Language = language ?? "",
                Method = "capture",
                CreatedAt = stamp.Seconds,
            },
            segments,
            null);
    }
}
