// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mimic.Core;

/// <summary>Every error the backend reports. <see cref="Exception.Message"/> is the text to show.</summary>
public sealed class MimicException : Exception
{
    public MimicException(string message) : base(message) { }
}

/// <summary>Small file, path and text helpers the backend depends on.</summary>
internal static class Sys
{
    internal static readonly UTF8Encoding Utf8 = new(false);

    /// <summary><c>str::lines</c>: splits on \n, drops one trailing \r per line, and no empty last line.</summary>
    internal static List<string> Lines(string text)
    {
        var lines = new List<string>();
        if (text.Length == 0) return lines;
        var parts = text.Split('\n');
        var count = text.EndsWith('\n') ? parts.Length - 1 : parts.Length;
        for (var i = 0; i < count; i++)
        {
            var line = parts[i];
            lines.Add(line.EndsWith('\r') ? line[..^1] : line);
        }
        return lines;
    }

    internal static string[] SplitWhitespace(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A JSON string exactly as serde_json writes it.</summary>
    internal static string JsonString(string value)
    {
        var json = new StringBuilder(value.Length + 2);
        json.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (c < 0x20) json.Append("\\u00").Append(((int)c).ToString("x2"));
                    else json.Append(c);
                    break;
            }
        }
        return json.Append('"').ToString();
    }

    internal static string JsonArray(IEnumerable<string> values) =>
        "[" + string.Join(",", values.Select(JsonString)) + "]";

    /// <summary><c>PathBuf::join</c> with a relative piece, which is added exactly as written.</summary>
    internal static string Join(string basePath, string piece) =>
        basePath.Length == 0 || basePath.EndsWith('\\') || basePath.EndsWith('/')
            ? basePath + piece
            : basePath + "\\" + piece;

    /// <summary><c>Path::parent</c>, leaving the separators as they were written.</summary>
    internal static string? Parent(string path)
    {
        var end = path.TrimEnd('\\', '/');
        var cut = end.LastIndexOfAny(['\\', '/']);
        if (cut < 0) return null;
        var parent = end[..cut];
        return parent.EndsWith(':') || parent.Length == 0 ? end[..(cut + 1)] : parent;
    }

    internal static string ParentOrSelf(string path) => Parent(path) ?? path;

    internal static string[] Components(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();

    internal static bool StartsWith(string path, string root)
    {
        var parts = Components(path);
        var prefix = Components(root);
        return prefix.Length <= parts.Length && prefix.SequenceEqual(parts.Take(prefix.Length), StringComparer.Ordinal);
    }

    /// <summary><c>Path::strip_prefix</c>. Null when the path is not under the root.</summary>
    internal static string? StripPrefix(string path, string root)
    {
        if (!StartsWith(path, root)) return null;
        return string.Join("\\", Components(path).Skip(Components(root).Length));
    }

    internal static string? FileName(string path)
    {
        var parts = Components(path);
        if (parts.Length == 0) return null;
        var name = parts[^1];
        return name == ".." || name.EndsWith(':') ? null : name;
    }

    internal static string? FileStem(string path)
    {
        var name = FileName(path);
        if (name is null) return null;
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    internal static string? Extension(string path)
    {
        var name = FileName(path);
        if (name is null) return null;
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? null : name[(dot + 1)..];
    }

    internal static string WithExtension(string path, string extension)
    {
        var name = FileName(path);
        if (name is null) return path;
        var stem = FileStem(path)!;
        var end = path.TrimEnd('\\', '/');
        return end[..^name.Length] + stem + "." + extension;
    }

    internal static string TakeChars(string text, int count)
    {
        var taken = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (count-- <= 0) break;
            taken.Append(rune.ToString());
        }
        return taken.ToString();
    }

    internal static int CharCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>Time since 1970, in the 100-nanosecond steps Windows counts in.</summary>
    internal readonly record struct Stamp(long Ticks)
    {
        internal string Nanos => Ticks + "00";
        internal long Seconds => Ticks / TimeSpan.TicksPerSecond;
    }

    internal static Stamp Now() => new(Math.Max(0, DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks));

    private static long nextUnique = -1;

    /// <summary>A name no other running tool holds, since a tab can run two at once.</summary>
    internal static string UniqueName() => $"{Now().Nanos}-{Interlocked.Increment(ref nextUnique)}";

    /// <summary>An operating-system error as one line of text.</summary>
    internal static string OsMessage(int code) => $"{new Win32Exception(code).Message} (os error {code})";

    internal static string IoMessage(Exception error)
    {
        if (error is Win32Exception win32) return OsMessage(win32.NativeErrorCode);
        var result = unchecked((uint)error.HResult);
        if (error is IOException or UnauthorizedAccessException && (result & 0xFFFF0000) == 0x80070000)
        {
            return OsMessage((int)(result & 0xFFFF));
        }
        return error.Message;
    }

    internal static bool IsNotFound(Exception error)
    {
        var code = error is Win32Exception win32 ? win32.NativeErrorCode : (int)(unchecked((uint)error.HResult) & 0xFFFF);
        return error is FileNotFoundException or DirectoryNotFoundException || (error is Win32Exception && code is 2 or 3);
    }

    /// <summary>Runs file work, turning any failure into a readable message.</summary>
    internal static T Io<T>(Func<T> work, Func<string, string>? message = null)
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
            var text = IoMessage(error);
            throw new MimicException(message is null ? text : message(text));
        }
    }

    internal static void Io(Action work, Func<string, string>? message = null) =>
        Io(() => { work(); return true; }, message);

    internal static void Quietly(Action work)
    {
        try { work(); } catch { }
    }

    internal static void RemoveDirAll(string path) => Quietly(() => Directory.Delete(path, true));

    internal static void RemoveFile(string path) => Quietly(() => File.Delete(path));

    internal static long FileLength(string path) => new FileInfo(path).Length;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern unsafe uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint length, uint flags);

    /// <summary>
    /// <c>fs::canonicalize</c>: the real path of something that exists, in the <c>\\?\</c> form
    /// the library has always stored. Throws <see cref="Win32Exception"/> when it cannot be opened.
    /// </summary>
    internal static unsafe string Canonicalize(string path)
    {
        const uint shareAll = 0x1 | 0x2 | 0x4;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using var handle = CreateFileW(Path.GetFullPath(path), 0, shareAll, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* start = buffer) length = GetFinalPathNameByHandleW(handle, start, (uint)buffer.Length, 0);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length < buffer.Length) return new string(buffer, 0, (int)length);
            buffer = new char[length + 1];
        }
    }

    /// <summary>The same path without the <c>\\?\</c> prefix, for APIs that do not accept it.</summary>
    internal static string StripVerbatim(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}
