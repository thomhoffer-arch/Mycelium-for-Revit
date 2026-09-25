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

        /// <summary>The segment number where the current log generation's full state begins
        /// (the segment <c>BeginSnapshot</c>/<c>BeginNewGeneration</c> wrote its header into).
        /// Every later segment up to the active one is a CONTINUATION (a size-based rotation that
        /// carries a header but no full state) — a reader rebuilds the current state by reading
        /// from this segment forward, and retention never deletes it or anything after it.
        /// 0 = unknown (a state.json written before this field existed): retention then keeps
        /// every segment, the safe direction, until the next generation sets it.</summary>
        public long GenerationSegment { get; set; }

        public HashCache Cache { get; set; } = new();

        /// <summary>Which <c>state.&lt;gen&gt;.jsonl</c> delta journal this base was last
        /// compacted with — bumped by one on every compaction. Defaults to 0 so an old state.json
        /// written before the journal existed still loads (paired with journal generation 0, or
        /// none at all).</summary>
        public long JournalGeneration { get; set; }
    }
}
