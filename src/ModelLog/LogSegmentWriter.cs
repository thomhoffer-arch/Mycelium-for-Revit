using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// The append-only segment file itself: opens/creates the active plain-text <c>.jsonl</c>
    /// segment, appends complete lines with an immediate flush (crash safety — the handoff's
    /// rule #1: "append one complete line, then flush. A reader ignores a torn last line." A
    /// process crash between the write and the flush leaves nothing new on disk at all; this
    /// flushes to the OS, which survives OUR process dying — the failure mode this guards
    /// against — without paying for an fsync on every single line, which large-model snapshots
    /// (hundreds of thousands of lines) can't afford), and rotates + gzips a finished segment
    /// once it crosses the size threshold.
    /// </summary>
    public sealed class LogSegmentWriter : IDisposable
    {
        public const long RotateAtBytes = 64L * 1024 * 1024;

        private readonly string _dir;
        private StreamWriter? _writer;
        private FileStream? _fileStream;

        public long SegmentNumber { get; private set; }
        public string ActivePath => SegmentPath(SegmentNumber);

        public LogSegmentWriter(string dir, long startingSegment)
        {
            _dir = dir;
            SegmentNumber = startingSegment <= 0 ? DiscoverLatestSegment(dir) : startingSegment;
            Open();
        }

        public static long DiscoverLatestSegment(string dir)
        {
            if (!Directory.Exists(dir)) return 1;
            long max = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
                if (long.TryParse(Path.GetFileNameWithoutExtension(f), out var n) && n > max) max = n;
            foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl.gz"))
            {
                var stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(f));
                if (long.TryParse(stem, out var n) && n > max) max = n;
            }
            return max == 0 ? 1 : max;
        }

        /// <summary>The <c>seq</c> of the last complete line in a plain segment, or 0 when the
        /// file is missing/empty or its tail has no parseable line (a torn last line is
        /// ignored, same as any reader does).</summary>
        public static long LastSeqIn(string plainSegmentPath)
        {
            try
            {
                if (!File.Exists(plainSegmentPath)) return 0;
                using var fs = new FileStream(plainSegmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var take = (int)Math.Min(fs.Length, 256 * 1024);
                fs.Seek(-take, SeekOrigin.End);
                var buf = new byte[take];
                var read = 0;
                while (read < take) { var r = fs.Read(buf, read, take - read); if (r <= 0) break; read += r; }
                var lines = Encoding.UTF8.GetString(buf, 0, read).Split('\n');
                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    try
                    {
                        using var json = System.Text.Json.JsonDocument.Parse(line);
                        if (json.RootElement.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var v)) return v;
                    }
                    catch (System.Text.Json.JsonException) { /* torn or partial line — try the one before */ }
                }
            }
            catch (IOException) { }
            return 0;
        }

        private string SegmentPath(long n) => Path.Combine(_dir, $"{n:D6}.jsonl");

        private void Open()
        {
            Directory.CreateDirectory(_dir);
            _fileStream = new FileStream(ActivePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public long CurrentSizeBytes => _fileStream?.Length ?? 0;

        /// <summary>Appends one complete JSON line + <c>\n</c>, then flushes.</summary>
        public void AppendLine(string json)
        {
            _writer!.Write(json);
            _writer.Write('\n');
            _writer.Flush();
        }

        public bool ShouldRotate() => CurrentSizeBytes >= RotateAtBytes;

        /// <summary>Closes the active segment, gzips it, and opens the next one. The caller
        /// (<see cref="ModelLogWriter"/>) is responsible for writing a fresh header + full
        /// snapshot as the first lines of the new segment, so every segment can be read on its
        /// own — the handoff's rotation rule.</summary>
        public void Rotate()
        {
            var finished = ActivePath;
            _writer!.Dispose();
            _fileStream!.Dispose();
            _writer = null;
            _fileStream = null;

            GzipAndDelete(finished);

            SegmentNumber++;
            Open();
        }

        private static void GzipAndDelete(string plainPath)
        {
            var gzPath = plainPath + ".gz";
            using (var src = File.OpenRead(plainPath))
            using (var dst = File.Create(gzPath))
            using (var gz = new GZipStream(dst, CompressionLevel.Optimal))
                src.CopyTo(gz);
            File.Delete(plainPath);
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _fileStream?.Dispose();
        }
    }
}
