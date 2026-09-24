using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// The append-only segment file itself: opens/creates the active plain-text <c>.jsonl</c>
    /// segment, appends complete lines (crash safety — the handoff's rule #1: "append one
    /// complete line, then flush. A reader ignores a torn last line." A process crash between
    /// the write and the flush leaves nothing new on disk at all; flushing to the OS survives
    /// OUR process dying — the failure mode this guards against). <see cref="AppendLine"/>
    /// itself no longer flushes on every call — measured as a real cost on a large snapshot
    /// (tens of thousands of syscalls) — <see cref="Flush"/> is called once per idle slice
    /// instead (see <c>ModelLogWriter.FlushJournalBuffer</c>), which still satisfies rule #1 at
    /// that coarser grain: nothing is ever reported written (a checkpoint, a session record, …)
    /// before its lines were flushed. Rotates a finished segment once it crosses the size
    /// threshold.
    ///
    /// Gzip runs off this thread: <see cref="Rotate"/> closes the finished segment and opens the
    /// next one immediately (so writing resumes at once), then hands the finished file to a
    /// background <see cref="Task"/> — no Revit API is involved in compressing a plain file, so
    /// there is no reason to freeze Revit's UI thread for the 1-2s a 64 MB segment can take. The
    /// background task writes <c>NNNNNN.jsonl.gz.tmp</c>, then renames it to <c>.gz</c>, then
    /// deletes the plain file — in that order, so a reader (or a crash) only ever sees either the
    /// complete plain file or the complete <c>.gz</c>, never a half-written one of either.
    /// </summary>
    public sealed class LogSegmentWriter : IDisposable
    {
        public const long RotateAtBytes = 64L * 1024 * 1024;

        // Best-effort bound on how long Dispose waits for in-flight compression — long enough
        // that a normal close finishes it (so the next startup finds nothing to redo), short
        // enough that a slow disk can never hang Revit's shutdown on this.
        private static readonly TimeSpan DisposeCompressionWait = TimeSpan.FromSeconds(5);

        private readonly string _dir;
        private StreamWriter? _writer;
        private FileStream? _fileStream;

        private readonly object _pendingLock = new();
        private readonly List<Task> _pendingCompressions = new();

        public long SegmentNumber { get; private set; }
        public string ActivePath => SegmentPath(SegmentNumber);

        public LogSegmentWriter(string dir, long startingSegment)
        {
            _dir = dir;
            SegmentNumber = startingSegment <= 0 ? DiscoverLatestSegment(dir) : startingSegment;
            RecoverPendingCompressions();
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

        /// <summary>Flushes first — <see cref="AppendLine"/> no longer flushes per line, so
        /// unflushed bytes still sitting in the <see cref="StreamWriter"/>'s own buffer wouldn't
        /// otherwise show up in <see cref="FileStream.Length"/> yet, and a caller (<see
        /// cref="ShouldRotate"/>, <c>ModelLogWriter.BeginNewGeneration</c>'s own "does this
        /// segment already have content" check) needs the TRUE current size, not a stale
        /// under-count. Only paid at these infrequent, pass-boundary checks — never per
        /// record.</summary>
        public long CurrentSizeBytes
        {
            get
            {
                _writer?.Flush();
                return _fileStream?.Length ?? 0;
            }
        }

        /// <summary>Appends one complete JSON line + <c>\n</c> — buffered, NOT flushed (see
        /// <see cref="Flush"/>). Flushing after every single line was measured as a real cost on
        /// a large snapshot (tens of thousands of small syscalls); the crash-safety rule this
        /// used to satisfy on its own ("append one complete line, then flush") now holds at the
        /// coarser grain of one flush per idle slice instead — still well within the "a reader
        /// ignores a torn last line" guarantee, since nothing this buffers is ever reported
        /// written (a checkpoint, a session record, …) without <see cref="Flush"/> having run
        /// first (see <c>ModelLogWriter.FlushJournalBuffer</c>).</summary>
        public void AppendLine(string json)
        {
            _writer!.Write(json);
            _writer.Write('\n');
        }

        /// <summary>Flushes any buffered lines to the OS — cheap (no fsync), survives OUR
        /// process dying, which is the failure mode this guards against. Called once per idle
        /// slice (<c>ModelLogWriter.FlushState</c>) and before anything (a checkpoint, a session
        /// record, a rotation) persists state that depends on those lines already being durable
        /// — see crash-safety rule #2.</summary>
        public void Flush() => _writer?.Flush();

        public bool ShouldRotate() => CurrentSizeBytes >= RotateAtBytes;

        /// <summary>Closes the active segment and opens the next one right away, then compresses
        /// the finished one on a background <see cref="Task"/> (see the class comment) — writing
        /// resumes immediately, never blocked on gzip. The caller (<see cref="ModelLogWriter"/>)
        /// is responsible for writing a fresh header + full snapshot as the first lines of the
        /// new segment, so every segment can be read on its own — the handoff's rotation
        /// rule.</summary>
        public void Rotate()
        {
            var finishedSegment = SegmentNumber;
            _writer!.Dispose();
            _fileStream!.Dispose();
            _writer = null;
            _fileStream = null;

            SegmentNumber++;
            Open();

            StartCompression(finishedSegment);
        }

        /// <summary>Called once from the constructor: finishes any background compression a
        /// previous process crashed or exited before completing. A rotated-away plain segment
        /// (its number below the freshly-resumed active one) with no matching <c>.gz</c> is
        /// recompressed, same as an ordinary rotation; one whose <c>.gz</c> already exists (a
        /// crash after the rename but before the plain file was deleted) just has its plain file
        /// cleaned up; a stray <c>.gz.tmp</c> from an interrupted attempt is deleted first so a
        /// fresh one doesn't collide with it.</summary>
        private void RecoverPendingCompressions()
        {
            if (!Directory.Exists(_dir)) return;

            foreach (var tmp in Directory.EnumerateFiles(_dir, "*.jsonl.gz.tmp"))
            {
                try { File.Delete(tmp); } catch { /* best-effort — retried next startup */ }
            }

            foreach (var plain in Directory.EnumerateFiles(_dir, "*.jsonl"))
            {
                if (!long.TryParse(Path.GetFileNameWithoutExtension(plain), out var n) || n >= SegmentNumber)
                    continue; // the active segment, or an unrecognized file — leave alone

                if (File.Exists(plain + ".gz"))
                    try { File.Delete(plain); } catch { /* best-effort — retried next startup */ }
                else
                    StartCompression(n);
            }
        }

        private void StartCompression(long segmentNumber)
        {
            var task = Task.Run(() => CompressAndCleanup(segmentNumber));
            lock (_pendingLock)
            {
                _pendingCompressions.RemoveAll(t => t.IsCompleted);
                _pendingCompressions.Add(task);
            }
        }

        private void CompressAndCleanup(long segmentNumber)
        {
            var plain = SegmentPath(segmentNumber);
            var gz = plain + ".gz";
            var tmp = gz + ".tmp";
            try
            {
                using (var src = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var dst = File.Create(tmp))
                using (var gzStream = new GZipStream(dst, CompressionLevel.Optimal))
                    src.CopyTo(gzStream);

                if (File.Exists(gz)) File.Delete(gz); // leftover from an earlier interrupted attempt
                File.Move(tmp, gz);
                File.Delete(plain);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: the plain file (and/or a leftover .gz.tmp) is simply left for the
                // next startup's RecoverPendingCompressions to retry — never thrown from a
                // background task with nothing to observe it.
            }
        }

        /// <summary>Test-only hook: blocks until every background compression this instance has
        /// started (via <see cref="Rotate"/> or startup recovery) has finished, or <paramref
        /// name="timeout"/> elapses. Returns false on timeout.</summary>
        internal bool WaitForBackgroundCompression(TimeSpan timeout)
        {
            Task[] pending;
            lock (_pendingLock) pending = _pendingCompressions.ToArray();
            return pending.Length == 0 || Task.WaitAll(pending, timeout);
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _fileStream?.Dispose();

            // Best-effort: give any in-flight compression a bounded chance to finish before this
            // process exits, so an ordinary close usually leaves nothing for the next startup's
            // RecoverPendingCompressions to redo. Never waited on indefinitely — Dispose must not
            // hang Revit's shutdown over a slow disk.
            Task[] pending;
            lock (_pendingLock) pending = _pendingCompressions.ToArray();
            try { Task.WaitAll(pending, DisposeCompressionWait); } catch { /* redone next startup */ }
        }
    }
}
