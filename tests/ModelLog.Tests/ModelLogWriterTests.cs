using Loam.Revit.Connector.ModelLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace ModelLog.Tests
{
    public class ModelLogWriterTests : IDisposable
    {
        private readonly string _root;

        public ModelLogWriterTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "model-log-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
        }

        private static JsonObject El(double length, double height, string mark) => new()
        {
            ["h"] = new JsonObject { ["mark"] = mark },
            ["q"] = new JsonObject { ["length"] = length, ["height"] = height },
        };

        [Fact]
        public void Append_IncrementsSeqAndWritesLine()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            var seq1 = w.Append(RecordKinds.Project, new JsonObject { ["number"] = "2233" });
            var seq2 = w.Append(RecordKinds.Project, new JsonObject { ["number"] = "2234" });

            Assert.Equal(1, seq1);
            Assert.Equal(2, seq2);
            Assert.Equal(2, w.LastSeq);
        }

        [Fact]
        public void WriteHeader_OnlyOncePerSegment()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.WriteHeader(new JsonObject { ["title"] = "Test.rvt" });
            w.WriteHeader(new JsonObject { ["title"] = "Test.rvt" });

            var lines = File.ReadAllLines(Path.Combine(_root, "model-a", "000001.jsonl"));
            Assert.Single(lines, l => l.Contains("\"k\":\"header\""));
        }

        [Fact]
        public void WriteIfChanged_FirstSeen_WritesFullState()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            var wrote = w.WriteIfChanged(RecordKinds.El, "guid-1", El(20.997, 10.006, "W-12"));

            Assert.True(wrote);
            Assert.Equal(1, w.LastSeq);
        }

        [Fact]
        public void WriteIfChanged_NoChange_WritesNothing()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.WriteIfChanged(RecordKinds.El, "guid-1", El(20.997, 10.006, "W-12"));
            var seqAfterFirst = w.LastSeq;

            var wroteAgain = w.WriteIfChanged(RecordKinds.El, "guid-1", El(20.997, 10.006, "W-12"));

            Assert.False(wroteAgain);
            Assert.Equal(seqAfterFirst, w.LastSeq);
        }

        [Fact]
        public void WriteIfChanged_OnlyChangedFieldGroupWritten()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.WriteIfChanged(RecordKinds.El, "guid-1", El(20.997, 10.006, "W-12"));

            // Only the quantity changed (a fire-rating-style edit would leave q/h alone; here we
            // flip q but keep h identical) — mark (h) must not be re-written.
            var updated = El(20.997, 12.5, "W-12");
            var seqBefore = w.LastSeq;
            var wrote = w.WriteIfChanged(RecordKinds.El, "guid-1", updated);
            Assert.True(wrote);
            Assert.Equal(seqBefore + 1, w.LastSeq);

            var lastLine = File.ReadAllLines(Path.Combine(_root, "model-a", "000001.jsonl")).Last();
            var parsed = JsonNode.Parse(lastLine)!.AsObject();
            Assert.True(parsed.ContainsKey("q"));
            Assert.False(parsed.ContainsKey("h")); // unchanged field-group omitted from the partial write
        }

        [Fact]
        public void WriteIfChanged_ClearedFieldGroup_ListedInUnset()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.WriteIfChanged(RecordKinds.El, "guid-1", El(20.997, 10.006, "W-12"));

            var cleared = new JsonObject { ["q"] = new JsonObject { ["length"] = 20.997, ["height"] = 10.006 } };
            w.WriteIfChanged(RecordKinds.El, "guid-1", cleared);

            var lastLine = File.ReadAllLines(Path.Combine(_root, "model-a", "000001.jsonl")).Last();
            var parsed = JsonNode.Parse(lastLine)!.AsObject();
            var unset = parsed["unset"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Assert.Contains("h", unset);
        }

        [Fact]
        public void WriteDelete_WithoutElementId_OmitsEidField()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.WriteIfChanged(RecordKinds.El, "guid-1", El(20.997, 10.006, "W-12"));
            w.WriteDelete("guid-1");

            var lastLine = File.ReadAllLines(Path.Combine(_root, "model-a", "000001.jsonl")).Last();
            var parsed = JsonNode.Parse(lastLine)!.AsObject();
            Assert.Equal("del", parsed["k"]!.GetValue<string>());
            Assert.Equal("guid-1", parsed["id"]!.GetValue<string>());
            Assert.False(parsed.ContainsKey("eid"));
        }

        [Fact]
        public void WriteDelete_WithElementId_IncludesEidField()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.WriteDelete("guid-2", 303793);

            var lastLine = File.ReadAllLines(Path.Combine(_root, "model-a", "000001.jsonl")).Last();
            var parsed = JsonNode.Parse(lastLine)!.AsObject();
            Assert.Equal(303793, parsed["eid"]!.GetValue<long>());
        }

        [Fact]
        public void KnownElementIdsNotIn_ReportsMissingElement()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.WriteIfChanged(RecordKinds.El, "guid-1", El(20.997, 10.006, "W-12"));
            w.WriteIfChanged(RecordKinds.El, "guid-2", El(5, 5, "W-13"));

            var stale = w.KnownElementIdsNotIn(new HashSet<string> { "guid-1" });

            Assert.Equal(new[] { "guid-2" }, stale);
        }

        [Fact]
        public void WriteIfUnseen_WritesOnceThenNeverAgain()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            var first = w.WriteIfUnseen(RecordKinds.Pdef, "builtin:FIRE_RATING", new JsonObject { ["name"] = "Fire Rating" });
            var second = w.WriteIfUnseen(RecordKinds.Pdef, "builtin:FIRE_RATING", new JsonObject { ["name"] = "Fire Rating" });

            Assert.True(first);
            Assert.False(second);
            Assert.Equal(1, w.LastSeq);
        }

        [Fact]
        public void Checkpoint_ClosedFlag_PersistsAcrossReopen()
        {
            using (var w = new ModelLogWriter(_root, "model-a"))
            {
                w.WriteCheckpoint(complete: true, modelVersion: "v1", elementCount: 10, closed: true);
            }

            using var reopened = new ModelLogWriter(_root, "model-a");
            Assert.True(reopened.LastCheckpointClosed);
        }

        [Fact]
        public void GapWritten_WhenPreviousSessionLeftNoClosedCheckpoint()
        {
            using (var w = new ModelLogWriter(_root, "model-a"))
            {
                w.Append(RecordKinds.Project, new JsonObject { ["number"] = "2233" });
                w.WriteCheckpoint(complete: true, modelVersion: "v1", elementCount: 10, closed: false);
            }

            using var reopened = new ModelLogWriter(_root, "model-a");
            var seqBefore = reopened.LastSeq;
            reopened.WriteGapIfNeeded("no closed checkpoint from previous session");

            Assert.Equal(seqBefore + 1, reopened.LastSeq);
        }

        [Fact]
        public void NoGap_WhenPreviousSessionClosedCleanly()
        {
            using (var w = new ModelLogWriter(_root, "model-a"))
            {
                w.WriteCheckpoint(complete: true, modelVersion: "v1", elementCount: 10, closed: true);
            }

            using var reopened = new ModelLogWriter(_root, "model-a");
            var seqBefore = reopened.LastSeq;
            reopened.WriteGapIfNeeded("should not fire");

            Assert.Equal(seqBefore, reopened.LastSeq);
        }

        [Fact]
        public void SecondWriter_SameModel_LockHeldElsewhere()
        {
            using var w1 = new ModelLogWriter(_root, "model-a");
            using var w2 = new ModelLogWriter(_root, "model-a");

            Assert.False(w1.LockHeldElsewhere);
            Assert.True(w2.LockHeldElsewhere);
        }

        [Fact]
        public void LogSegmentWriter_Rotate_ProducesGzippedFileAndNextSegment()
        {
            var dir = Path.Combine(_root, "segments");
            var seg = new LogSegmentWriter(dir, 1);
            seg.AppendLine("{\"seq\":1}");
            seg.Rotate();
            seg.AppendLine("{\"seq\":2}");
            seg.Dispose();

            Assert.True(File.Exists(Path.Combine(dir, "000001.jsonl.gz")));
            Assert.True(File.Exists(Path.Combine(dir, "000002.jsonl")));
            Assert.False(File.Exists(Path.Combine(dir, "000001.jsonl")));

            using var gz = new GZipStream(File.OpenRead(Path.Combine(dir, "000001.jsonl.gz")), CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            Assert.Equal("{\"seq\":1}", reader.ReadLine());
        }

        [Fact]
        public void DiscoverLatestSegment_ResumesAfterGzippedSegments()
        {
            var dir = Path.Combine(_root, "resume");
            using (var seg = new LogSegmentWriter(dir, 1))
            {
                seg.AppendLine("{\"seq\":1}");
                seg.Rotate();
                seg.AppendLine("{\"seq\":2}");
            }

            // startingSegment <= 0 triggers directory discovery.
            using var resumed = new LogSegmentWriter(dir, 0);
            Assert.Equal(2, resumed.SegmentNumber);
        }
    }
}
