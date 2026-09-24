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
        public string? LastProducerVersion { get; set; }

        /// <summary>The model version (GUID string) a checkpoint last confirmed the log matched
        /// EXACTLY — set only when that checkpoint was both <c>complete</c> and the document had
        /// no unsaved changes at that moment, and cleared on anything else (an interrupted pass,
        /// an unsaved edit since, or a new log generation). The only baseline
        /// <c>Document.GetChangedElements</c> may ever be trusted against — see
        /// ModelLogCapture.ModelLogService.ReconcileJob.</summary>
        public string? LastCompleteModelVersion { get; set; }

        public HashCache Cache { get; set; } = new();

        /// <summary>Which <c>state.&lt;gen&gt;.jsonl</c> delta journal this base was last
        /// compacted with — bumped by one on every compaction. Defaults to 0 so an old state.json
        /// written before the journal existed still loads (paired with journal generation 0, or
        /// none at all).</summary>
        public long JournalGeneration { get; set; }
    }
}
