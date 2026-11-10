// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Mimic.Core;

/// <summary>One tab's work: whether it is running, and the tools it has started.</summary>
public sealed class Job
{
    private int running;
    private int cancel;
    private int finishCapture;
    internal readonly List<Process> Children = [];

    /// <summary>Claims the single job slot. Returns false if a job is already running.</summary>
    public bool Start()
    {
        if (Interlocked.Exchange(ref running, 1) == 1) return false;
        Volatile.Write(ref cancel, 0);
        Volatile.Write(ref finishCapture, 0);
        return true;
    }

    public void Finish() => Volatile.Write(ref running, 0);

    public void RequestCancel()
    {
        Volatile.Write(ref cancel, 1);
        lock (Children)
        {
            foreach (var child in Children) Tools.KillTree(child);
        }
    }

    /// <summary>Ends a recording and moves on to transcribing it.</summary>
    public void RequestFinishCapture() => Volatile.Write(ref finishCapture, 1);

    internal bool FinishRequested => Volatile.Read(ref finishCapture) == 1;
    internal bool Cancelled => Volatile.Read(ref cancel) == 1;
}

/// <summary>A program to run, with its arguments.</summary>
internal sealed class ToolCommand(string program)
{
    internal string Program { get; } = program;
    internal List<string> Arguments { get; } = [];
    internal string? WorkingDirectory { get; set; }
    internal Dictionary<string, string> Environment { get; } = [];

    internal ToolCommand Args(params string[] arguments)
    {
        Arguments.AddRange(arguments);
        return this;
    }
}

internal static class Tools
{
    internal const string Cancelled = "Cancelled.";
    /// <summary>How every yt-dlp failure message begins.</summary>
    internal const string NotDownloadable = "yt-dlp failed";
    private const string Broken = "Job state is broken.";
    /// <summary>How long to wait for the last output once a tool has exited.</summary>
    private const int DrainMs = 2000;

    /// <summary>PowerShell running a script, passed encoded because this PC's execution policy blocks script files.</summary>
    internal static ToolCommand EncodedPowershell(string script) =>
        new ToolCommand("powershell").Args("-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

    internal static string Script(string name)
    {
        using var stream = typeof(Tools).GetTypeInfo().Assembly.GetManifestResourceStream(name)
            ?? throw new MimicException($"Mimic is missing its {name} script.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// yt-dlp.exe unpacks itself into a second process and starts ffmpeg, so killing
    /// only the first one would leave the others running.
    /// </summary>
    internal static void KillTree(Process child)
    {
        var killed = false;
        try
        {
            var start = new ProcessStartInfo("taskkill") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in (string[])["/T", "/F", "/PID", child.Id.ToString()]) start.ArgumentList.Add(argument);
            using var taskkill = Process.Start(start);
            if (taskkill is not null)
            {
                taskkill.WaitForExit();
                killed = taskkill.ExitCode == 0;
            }
        }
        catch
        {
        }
        if (!killed) Sys.Quietly(child.Kill);
    }

    /// <summary>Runs a tool to completion, returning stdout. Cancelling kills its process tree.</summary>
    internal static string Run(Job job, ToolCommand command, string tool) => RunWithTimeout(job, command, tool, null, null);

    /// <summary>Same as <see cref="Run"/>, feeding the tool a file on stdin.</summary>
    internal static string RunWithInput(Job job, ToolCommand command, string tool, string? input) =>
        RunWithTimeout(job, command, tool, input, null);

    /// <summary>
    /// Runs a tool with an optional wall-clock limit. Large downloads and Whisper keep
    /// their unlimited default; only short metadata lookups should use a deadline.
    /// </summary>
    internal static string RunWithTimeout(Job job, ToolCommand command, string tool, string? input, TimeSpan? timeout)
    {
        if (job.Cancelled) throw new MimicException(Cancelled);
        var stdin = input is null ? null : Sys.Io(() => File.ReadAllBytes(input));
        var start = new ProcessStartInfo(command.Program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Sys.Utf8,
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        if (command.WorkingDirectory is not null) start.WorkingDirectory = command.WorkingDirectory;
        foreach (var (name, value) in command.Environment) start.Environment[name] = value;
        var workingDirectory = command.WorkingDirectory ?? Environment.CurrentDirectory;

        Process child;
        lock (job.Children)
        {
            if (job.Cancelled) throw new MimicException(Cancelled);
            try
            {
                child = Process.Start(start) ?? throw new InvalidOperationException("no process was started");
            }
            catch (Exception error)
            {
                throw new MimicException($"Could not start {tool} at '{command.Program}' from '{workingDirectory}': {SpawnMessage(error)}");
            }
            job.Children.Add(child);
        }

        var output = new MemoryStream();
        var errors = new MemoryStream();
        var readers = new[] { Pump(child.StandardOutput.BaseStream, output), Pump(child.StandardError.BaseStream, errors) };
        _ = Task.Run(() =>
        {
            try
            {
                if (stdin is not null) child.StandardInput.BaseStream.Write(stdin);
                child.StandardInput.Close();
            }
            catch
            {
            }
        });

        var started = Stopwatch.StartNew();
        var timedOut = false;
        int? status = null;
        while (true)
        {
            lock (job.Children)
            {
                if (!job.Children.Contains(child)) throw new MimicException(Broken);
                if (child.HasExited)
                {
                    job.Children.Remove(child);
                    status = child.ExitCode;
                    break;
                }
                if (timeout is { } limit && started.Elapsed >= limit)
                {
                    KillTree(child);
                    Sys.Quietly(child.WaitForExit);
                    job.Children.Remove(child);
                    timedOut = true;
                    break;
                }
            }
            Thread.Sleep(100);
        }

        // Something the tool started can keep its output open after it exits; that must not hold up the job.
        Task.WaitAll(readers, DrainMs);
        string stdout;
        string stderr;
        lock (output) stdout = Encoding.UTF8.GetString(output.ToArray());
        lock (errors) stderr = Encoding.UTF8.GetString(errors.ToArray());
        child.Dispose();

        if (job.Cancelled) throw new MimicException(Cancelled);
        if (timedOut)
        {
            var seconds = (long)(timeout?.TotalSeconds ?? 0);
            throw new MimicException($"{NotDownloadable}: timed out reading the video's details after {seconds} seconds.");
        }
        if (status != 0) throw new MimicException(ToolError(tool, stderr.Trim().Length == 0 ? stdout : stderr));
        return stdout;
    }

    private static Task Pump(Stream source, MemoryStream sink) => Task.Run(() =>
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                lock (sink) sink.Write(buffer, 0, read);
            }
        }
        catch
        {
        }
    });

    private static string SpawnMessage(Exception error) =>
        error is Win32Exception { NativeErrorCode: 2 or 3 } ? "program not found" : Sys.IoMessage(error);

    internal static string ToolError(string tool, string stderr)
    {
        var lines = Sys.Lines(stderr);
        lines.Reverse();
        var line = lines.FirstOrDefault(l => l.Contains("ERROR"))
            ?? lines.FirstOrDefault(l => l.Trim().Length > 0)
            ?? "it stopped without saying why";
        return $"{tool} failed: {line.Trim()}";
    }
}
