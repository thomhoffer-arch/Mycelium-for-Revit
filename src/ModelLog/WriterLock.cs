using System;
using System.IO;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// One writer per model (the handoff's crash-safety rule #4): holds an exclusive file lock
    /// for the lifetime of this Revit session's <see cref="ModelLogWriter"/>, so a second Revit
    /// session opening the same model detects the collision via <see cref="TryAcquire"/>
    /// returning null and skips writing instead of interleaving two writers into one log.
    /// </summary>
    public sealed class WriterLock : IDisposable
    {
        private FileStream? _stream;
        public string Path { get; }

        private WriterLock(string path, FileStream stream)
        {
            Path = path;
            _stream = stream;
        }

        /// <summary>Returns null when another process already holds the lock.</summary>
        public static WriterLock? TryAcquire(string path)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new WriterLock(path, stream);
            }
            catch (IOException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
