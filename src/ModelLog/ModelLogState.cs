namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// The full contents of <c>state.json</c> — everything needed to resume writing a model's
    /// log without re-deriving it from scratch: the seq counter, which segment is active, the
    /// last checkpoint's closed flag (the crash-vs-clean-close signal on the next open), and the
    /// hash cache. A plain POCO so <c>System.Text.Json</c>'s default reflection-based serializer
    /// round-trips it with no custom converters.
    /// </summary>
    public sealed class ModelLogState
    {
        public long LastSeq { get; set; }
        public long CurrentSegment { get; set; } = 1;
        public bool LastCheckpointClosed { get; set; }
        public string? LastModelVersion { get; set; }
        public HashCache Cache { get; set; } = new();
    }
}
