// Mimic by soybean_alien
// https://github.com/soybean-alien/Mimic
// Licensed under the GNU General Public License v3.0 (see LICENSE).
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mimic.Core;

/// <summary>The local library: one SQLite file holding every transcript.</summary>
public sealed class Store : IDisposable
{
    private const string Gone = "That transcript is no longer in the library.";
    private readonly SqliteConnection conn;

    private Store(SqliteConnection conn) => this.conn = conn;

    /// <summary>Writes a complete copy of the library to a new file.</summary>
    public void CopyTo(string path)
    {
        try
        {
            using var command = conn.CreateCommand();
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
        catch (SqliteException error)
        {
            throw new MimicException($"Could not copy the library: {error.Message}");
        }
    }

    public void Dispose() => conn.Dispose();

    /// <summary>Removes trailing codec, resolution, frame-rate, and audio metadata from a local filename title.</summary>
    public static string CleanLocalVideoTitle(string raw)
    {
        var title = raw.Trim();
        var start = Math.Max(title.LastIndexOf('('), title.LastIndexOf('['));
        if (start >= 0)
        {
            var suffix = title[start..].Trim();
            var isClosed = (suffix.StartsWith('(') && suffix.EndsWith(')')) || (suffix.StartsWith('[') && suffix.EndsWith(']'));
            string[] technicalMarkers =
            [
                "144p", "240p", "360p", "480p", "720p", "1080p", "1440p", "2160p", "4320p", "fps",
                "av1", "avc", "h264", "h.264", "h265", "h.265", "hevc", "vp9", "vp09", "aac", "opus",
                "mp3", "kbit", "kbps", "khz", "stereo", "mono", "10bit", "8bit",
            ];
            var lowered = suffix.ToLowerInvariant();
            if (isClosed && technicalMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal)))
            {
                title = title[..start];
                var end = title.Length;
                while (end > 0 && (char.IsWhiteSpace(title[end - 1]) || title[end - 1] is '-' or '_' or '.')) end--;
                title = title[..end];
            }
        }
        return title.Length == 0 ? "Local video" : title;
    }

    public static Store Open(string path)
    {
        return Sql(() =>
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            try
            {
                conn.Open();
                var store = new Store(conn);
                store.Prepare();
                return store;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        });
    }

    private void Prepare()
    {
        Execute(
            """
            PRAGMA foreign_keys = ON;
             CREATE TABLE IF NOT EXISTS videos (
                 id TEXT PRIMARY KEY,
                 url TEXT NOT NULL,
                 local_video_path TEXT,
                 media_hosts_json TEXT NOT NULL DEFAULT '[]',
                 title TEXT NOT NULL,
                 channel TEXT NOT NULL,
                 duration_seconds INTEGER NOT NULL,
                 transcode_seconds INTEGER NOT NULL DEFAULT 0,
                 language TEXT NOT NULL,
                 method TEXT NOT NULL,
                 created_at INTEGER NOT NULL
             );
             CREATE TABLE IF NOT EXISTS segments (
                 video_id TEXT NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
                 position INTEGER NOT NULL,
                 start_ms INTEGER NOT NULL,
                 end_ms INTEGER NOT NULL,
                 text TEXT NOT NULL,
                 PRIMARY KEY (video_id, position)
             );
             CREATE TABLE IF NOT EXISTS section_titles (
                 video_id TEXT NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
                 start_ms INTEGER NOT NULL,
                 title TEXT NOT NULL,
                 PRIMARY KEY (video_id, start_ms)
             );
             CREATE TABLE IF NOT EXISTS image_scores (
                 video_id TEXT PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE,
                 score TEXT NOT NULL
             );
             CREATE TABLE IF NOT EXISTS video_takeaways (
                 video_id TEXT PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE,
                 text TEXT NOT NULL,
                 updated_at INTEGER NOT NULL
             );
             CREATE TABLE IF NOT EXISTS settings (
                 name TEXT PRIMARY KEY,
                 value TEXT NOT NULL
             );
            """);

        if (!HasVideoColumn("transcode_seconds"))
        {
            Execute("ALTER TABLE videos ADD COLUMN transcode_seconds INTEGER NOT NULL DEFAULT 0;");
        }
        if (!HasVideoColumn("local_video_path"))
        {
            Execute("ALTER TABLE videos ADD COLUMN local_video_path TEXT;");
        }
        if (!HasVideoColumn("media_hosts_json"))
        {
            Execute("ALTER TABLE videos ADD COLUMN media_hosts_json TEXT NOT NULL DEFAULT '[]';");
        }

        // Clean older local-video titles when the library opens, so existing notes receive
        // the same filename cleanup as newly imported files.
        var localTitles = Query(
            "SELECT id, title FROM videos WHERE method != 'image' AND url NOT LIKE 'http://%' AND url NOT LIKE 'https://%'",
            row => (Id: row.GetString(0), Title: row.GetString(1)));
        foreach (var (id, title) in localTitles)
        {
            var cleaned = CleanLocalVideoTitle(title);
            if (cleaned != title) Execute("UPDATE videos SET title = $1 WHERE id = $2", null, cleaned, id);
        }
    }

    private bool HasVideoColumn(string name) =>
        Query("PRAGMA table_info(videos)", row => row.GetString(1)).Contains(name);

    /// <summary>Saves a transcript, replacing any earlier one for the same video.</summary>
    public void Save(Transcript transcript) => Sql(() =>
    {
        using var tx = conn.BeginTransaction();
        var v = transcript.Video;
        Execute("DELETE FROM videos WHERE id = $1", tx, v.Id);
        Execute(
            """
            INSERT INTO videos (id, url, local_video_path, media_hosts_json, title, channel, duration_seconds, transcode_seconds, language, method, created_at)
                         VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """,
            tx, v.Id, v.Url, v.LocalVideoPath, Sys.JsonArray(v.MediaHosts), v.Title, v.Channel, v.DurationSeconds,
            v.TranscodeSeconds, v.Language, v.Method, v.CreatedAt);
        for (var i = 0; i < transcript.Segments.Count; i++)
        {
            var s = transcript.Segments[i];
            Execute("INSERT INTO segments VALUES ($1, $2, $3, $4, $5)", tx, v.Id, (long)i, s.StartMs, s.EndMs, s.Text);
        }
        tx.Commit();
    });

    public List<Video> List() => Sql(() => Query(
        """
        SELECT id, url, local_video_path, media_hosts_json, title, channel, duration_seconds, transcode_seconds, language, method, created_at
                         FROM videos ORDER BY created_at DESC
        """,
        row => new Video
        {
            Id = row.GetString(0),
            Url = row.GetString(1),
            LocalVideoPath = row.IsDBNull(2) ? null : row.GetString(2),
            MediaHosts = Hosts(row.GetString(3)),
            Title = row.GetString(4),
            Channel = row.GetString(5),
            DurationSeconds = row.GetInt64(6),
            TranscodeSeconds = row.GetInt64(7),
            Language = row.GetString(8),
            Method = row.GetString(9),
            CreatedAt = row.GetInt64(10),
        }));

    private static List<string> Hosts(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var hosts = new List<string>();
            foreach (var host in document.RootElement.EnumerateArray())
            {
                if (host.ValueKind != JsonValueKind.String) return [];
                hosts.Add(host.GetString()!);
            }
            return hosts;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    public Transcript Get(string id) => Sql(() =>
    {
        var video = List().FirstOrDefault(v => v.Id == id) ?? throw new MimicException(Gone);
        var segments = Query(
            "SELECT start_ms, end_ms, text FROM segments WHERE video_id = $1 ORDER BY position",
            row => new Segment(row.GetInt64(0), row.GetInt64(1), row.GetString(2)),
            id);
        return new Transcript(video, segments, null);
    });

    public void UpdateTitle(string id, string title) => Sql(() =>
    {
        if (Execute("UPDATE videos SET title = $1 WHERE id = $2", null, title, id) == 0) throw new MimicException(Gone);
    });

    public void SaveTitles(string id, IReadOnlyList<(long StartMs, string Title)> titles) => Sql(() =>
    {
        using var tx = conn.BeginTransaction();
        Execute("DELETE FROM section_titles WHERE video_id = $1", tx, id);
        foreach (var (startMs, title) in titles)
        {
            Execute("INSERT INTO section_titles VALUES ($1, $2, $3)", tx, id, startMs, title);
        }
        tx.Commit();
    });

    /// <summary>Section titles for a video, by the start of their passage.</summary>
    public Dictionary<long, string> Titles(string id) => Sql(() =>
    {
        var titles = new Dictionary<long, string>();
        foreach (var (startMs, title) in Query(
            "SELECT start_ms, title FROM section_titles WHERE video_id = $1",
            row => (row.GetInt64(0), row.GetString(1)),
            id))
        {
            titles[startMs] = title;
        }
        return titles;
    });

    public void SaveScore(string id, ImageScore score) =>
        Sql(() => Execute("INSERT OR REPLACE INTO image_scores VALUES ($1, $2)", null, id, score.ToJson()));

    public ImageScore? Score(string id) => Sql(() =>
    {
        var json = Query("SELECT score FROM image_scores WHERE video_id = $1", row => row.GetString(0), id).FirstOrDefault();
        if (json is null) return null;
        try
        {
            return ImageScore.FromJson(json);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            throw new MimicException(error.Message);
        }
    });

    public void SaveTakeaways(string id, string text) => Sql(() => Execute(
        "INSERT OR REPLACE INTO video_takeaways (video_id, text, updated_at) VALUES ($1, $2, $3)",
        null, id, text, Sys.Now().Seconds));

    public string? Takeaways(string id) =>
        Sql(() => Query("SELECT text FROM video_takeaways WHERE video_id = $1", row => row.GetString(0), id).FirstOrDefault());

    public void SaveModelChoice(ModelChoice choice) =>
        Sql(() => Execute("INSERT OR REPLACE INTO settings VALUES ('model', $1)", null, choice.ToJson()));

    public ModelChoice GetModelChoice() => Sql(() =>
    {
        var json = Query("SELECT value FROM settings WHERE name = 'model'", row => row.GetString(0)).FirstOrDefault();
        return json is null ? ModelChoice.Default : ModelChoice.FromJson(json);
    });

    public void Delete(string id) => Sql(() => Execute("DELETE FROM videos WHERE id = $1", null, id));

    /// <summary>
    /// Copy playback files from the former bundled-resource downloads folder into app-local
    /// storage, then update only their path column. Source files are intentionally retained.
    /// </summary>
    public (int Migrated, int Failed) MigrateLegacyDownloads(string legacyDir, string destinationDir, string backupRoot)
    {
        string? legacyRoot;
        try
        {
            legacyRoot = Sys.Canonicalize(legacyDir);
        }
        catch (Exception error) when (Sys.IsNotFound(error))
        {
            legacyRoot = null;
        }
        catch (Exception error)
        {
            throw new MimicException(Sys.IoMessage(error));
        }
        var destinationRoot = Sys.Io(() => Sys.Canonicalize(destinationDir));
        var migrated = 0;
        var failed = 0;

        foreach (var video in List())
        {
            if (video.LocalVideoPath is not { } storedPath) continue;
            (string Source, string Relative)? sourceAndRelative;
            try
            {
                var path = Sys.Canonicalize(storedPath);
                sourceAndRelative = File.Exists(path) && legacyRoot is not null && Sys.StartsWith(path, legacyRoot)
                    ? (path, Sys.StripPrefix(path, legacyRoot)!)
                    : PreservedLegacyDownload(storedPath, backupRoot);
            }
            catch (Exception error) when (Sys.IsNotFound(error))
            {
                sourceAndRelative = PreservedLegacyDownload(storedPath, backupRoot);
            }
            catch
            {
                failed++;
                continue;
            }
            if (sourceAndRelative is not var (source, relative)) continue;
            if (!File.Exists(source) || Sys.StartsWith(source, destinationRoot)) continue;
            if (Sys.FileName(relative) is not { } originalName)
            {
                failed++;
                continue;
            }

            var stem = Sys.FileStem(relative) ?? "video";
            var extension = Sys.Extension(relative) is { } found ? $".{found}" : "";
            long sourceLen;
            try
            {
                sourceLen = Sys.FileLength(source);
            }
            catch
            {
                failed++;
                continue;
            }

            var suffix = 0UL;
            string? target = null;
            long copied = 0;
            while (true)
            {
                var candidate = suffix == 0
                    ? Sys.Join(destinationRoot, originalName)
                    : Sys.Join(destinationRoot, $"{stem}.mimic-{suffix}{extension}");
                FileStream targetFile;
                try
                {
                    targetFile = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException error) when ((error.HResult & 0xFFFF) is 80 or 183)
                {
                    if (FilesEqualOrFalse(source, candidate))
                    {
                        target = candidate;
                        copied = sourceLen;
                        break;
                    }
                    suffix++;
                    continue;
                }
                catch
                {
                    failed++;
                    break;
                }
                try
                {
                    using (targetFile)
                    using (var sourceFile = File.OpenRead(source))
                    {
                        sourceFile.CopyTo(targetFile);
                        targetFile.Flush(true);
                        copied = targetFile.Length;
                    }
                    target = candidate;
                }
                catch
                {
                    Sys.RemoveFile(candidate);
                    failed++;
                }
                break;
            }
            if (target is null) continue;

            long targetLen;
            try { targetLen = Sys.FileLength(target); } catch { targetLen = -1; }
            if (copied != sourceLen || targetLen != sourceLen || !FilesEqualOrFalse(source, target))
            {
                Sys.RemoveFile(target);
                failed++;
                continue;
            }

            try
            {
                using var tx = conn.BeginTransaction();
                var updated = Execute(
                    "UPDATE videos SET local_video_path = $1 WHERE id = $2 AND local_video_path = $3",
                    tx, target, video.Id, storedPath);
                if (updated == 1)
                {
                    tx.Commit();
                    migrated++;
                }
                else
                {
                    // The copied file is harmless; keep the old source and leave the database row alone.
                    failed++;
                }
            }
            catch
            {
                failed++;
            }
        }

        return (migrated, failed);
    }

    private static bool FilesEqualOrFalse(string left, string right)
    {
        try
        {
            using var a = File.OpenRead(left);
            using var b = File.OpenRead(right);
            var leftBuffer = new byte[64 * 1024];
            var rightBuffer = new byte[64 * 1024];
            while (true)
            {
                var leftRead = a.ReadAtLeast(leftBuffer, leftBuffer.Length, false);
                var rightRead = b.ReadAtLeast(rightBuffer, rightBuffer.Length, false);
                if (leftRead != rightRead || !leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead))) return false;
                if (leftRead == 0) return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static (string Source, string Relative)? PreservedLegacyDownload(string sourcePath, string backupRoot)
    {
        IEnumerable<string> backups;
        try
        {
            backups = Directory.EnumerateFileSystemEntries(backupRoot).ToList();
        }
        catch
        {
            return null;
        }
        foreach (var backupDir in backups)
        {
            try
            {
                var info = new DirectoryInfo(backupDir);
                if (!info.Exists || info.LinkTarget is not null) continue;
                var sourceRoot = File.ReadAllText(Sys.Join(backupDir, ".mimic-source-root")).Trim();
                if (Sys.StripPrefix(sourcePath, sourceRoot) is not { } relative) continue;
                var canonical = Sys.Canonicalize(Sys.Join(backupDir, relative));
                var canonicalBackup = Sys.Canonicalize(backupDir);
                if (File.Exists(canonical) && Sys.StartsWith(canonical, canonicalBackup)) return (canonical, relative);
            }
            catch
            {
            }
        }
        return null;
    }

    private int Execute(string sql, SqliteTransaction? tx = null, params object?[] values)
    {
        using var command = Command(sql, tx, values);
        return command.ExecuteNonQuery();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params object?[] values)
    {
        using var command = Command(sql, null, values);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx, object?[] values)
    {
        var command = conn.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"${i + 1}", values[i] ?? DBNull.Value);
        return command;
    }

    private static T Sql<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (Exception error) when (error is SqliteException or InvalidCastException or InvalidOperationException)
        {
            throw new MimicException(error.Message);
        }
    }

    private static void Sql(Action work) => Sql(() => { work(); return true; });
}
