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
        public void RecordSession_WritesRecordAndPersistsLastProducerVersion()
        {
            using (var w = new ModelLogWriter(_root, "model-a"))
            {
                Assert.Null(w.LastProducerVersion);
                w.RecordSession("0.5.0", "2025");
                Assert.Equal("0.5.0", w.LastProducerVersion);

                var lastLine = File.ReadAllLines(Path.Combine(_root, "model-a", "000001.jsonl")).Last();
                var parsed = JsonNode.Parse(lastLine)!.AsObject();
                Assert.Equal("session", parsed["k"]!.GetValue<string>());
                Assert.Equal("0.5.0", parsed["producerVersion"]!.GetValue<string>());
                Assert.Equal("2025", parsed["revitVersion"]!.GetValue<string>());
            }

            using var reopened = new ModelLogWriter(_root, "model-a");
            Assert.Equal("0.5.0", reopened.LastProducerVersion);
        }

        [Fact]
        public void RecordSession_WithoutRevitVersion_OmitsField()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.RecordSession("0.5.0", null);

            var lastLine = File.ReadAllLines(Path.Combine(_root, "model-a", "000001.jsonl")).Last();
            var parsed = JsonNode.Parse(lastLine)!.AsObject();
            Assert.False(parsed.ContainsKey("revitVersion"));
        }

        private string StatePath => Path.Combine(_root, "model-a", "state.json");
        private string Seg(int n) => Path.Combine(_root, "model-a", $"{n:D6}.jsonl");
        private string Journal(long gen) => Path.Combine(_root, "model-a", $"state.{gen}.jsonl");

        /// <summary>Lowers ModelLogWriter.MinCompactionBytes for the duration of one test and
        /// restores it afterwards — the production default (1 MiB) would make a compaction test
        /// write an unreasonable number of records.</summary>
        private sealed class LowCompactionThreshold : IDisposable
        {
            private readonly long _previous;
            public LowCompactionThreshold(long bytes)
            {
                _previous = ModelLogWriter.MinCompactionBytes;
                ModelLogWriter.MinCompactionBytes = bytes;
            }
            public void Dispose() => ModelLogWriter.MinCompactionBytes = _previous;
        }

        [Fact]
        public void Appends_DoNotTouchStateJson_UntilCompaction()
        {
            using (var w = new ModelLogWriter(_root, "model-a"))
            {
                for (var i = 0; i < 10; i++) w.WriteIfChanged(RecordKinds.El, "guid-" + i, El(i, 1, "W"));
                w.FlushState();

                Assert.False(File.Exists(StatePath)); // no base yet — nowhere near MinCompactionBytes
                Assert.True(File.Exists(Journal(0)));
                Assert.True(new FileInfo(Journal(0)).Length > 0);
            }

            using var reopened = new ModelLogWriter(_root, "model-a");
            Assert.Equal(10, reopened.LastSeq);
            Assert.Equal(10, reopened.KnownElementIdsNotIn(new HashSet<string>()).Count);
            Assert.False(reopened.WriteIfChanged(RecordKinds.El, "guid-0", El(0, 1, "W"))); // hash cache restored
        }

        [Fact]
        public void NoOpReconcileThenCheckpoint_StateJsonUnchanged()
        {
            using (var lowered = new LowCompactionThreshold(2048))
            {
                using (var w = new ModelLogWriter(_root, "model-a"))
                {
                    for (var i = 0; i < 200; i++) w.WriteIfChanged(RecordKinds.El, "guid-" + i, El(i, 1, "W"));
                    w.WriteCheckpoint(complete: true, modelVersion: "v1", elementCount: 200, closed: false);
                }
                Assert.True(File.Exists(StatePath)); // compaction happened during that checkpoint

                var stamp = File.GetLastWriteTimeUtc(StatePath);
                var content = File.ReadAllBytes(StatePath);
                System.Threading.Thread.Sleep(50);

                using (var w = new ModelLogWriter(_root, "model-a"))
                {
                    var linesBefore = File.ReadAllLines(Seg(1)).Length;
                    for (var i = 0; i < 200; i++)
                        Assert.False(w.WriteIfChanged(RecordKinds.El, "guid-" + i, El(i, 1, "W"))); // nothing changed
                    w.WriteCheckpoint(complete: true, modelVersion: "v1", elementCount: 200, closed: false);

                    // Only the cp log line was appended — reconcile itself wrote nothing.
                    Assert.Equal(linesBefore + 1, File.ReadAllLines(Seg(1)).Length);
                }

                Assert.Equal(stamp, File.GetLastWriteTimeUtc(StatePath));
                Assert.Equal(content, File.ReadAllBytes(StatePath));
            }
        }

        [Fact]
        public void Compaction_TriggersAtCheckpoint_NewGenerationOldJournalGone()
        {
            using var lowered = new LowCompactionThreshold(2048);
            using (var w = new ModelLogWriter(_root, "model-a"))
            {
                for (var i = 0; i < 200; i++) w.WriteIfChanged(RecordKinds.El, "guid-" + i, El(i, 1, "W"));
                w.FlushState();
                Assert.True(new FileInfo(Journal(0)).Length > 2048); // past threshold before the checkpoint compacts it

                w.WriteCheckpoint(complete: true, modelVersion: "v1", elementCount: 200, closed: false);

                Assert.True(File.Exists(StatePath));
                Assert.False(File.Exists(Journal(0))); // old generation's journal is gone
            }

            using var reopened = new ModelLogWriter(_root, "model-a");
            for (var i = 0; i < 200; i++)
                Assert.False(reopened.WriteIfChanged(RecordKinds.El, "guid-" + i, El(i, 1, "W")));
        }

        [Fact]
        public void Compaction_SaveFailure_IsBestEffort_CheckpointStillSucceeds()
        {
            using var lowered = new LowCompactionThreshold(2048);
            using var w = new ModelLogWriter(_root, "model-a");
            for (var i = 0; i < 200; i++) w.WriteIfChanged(RecordKinds.El, "guid-" + i, El(i, 1, "W"));
            w.FlushState();

            // Force the compaction's tmp write to fail deterministically, without relying on
            // filesystem permissions: state.json.tmp is itself a directory.
            Directory.CreateDirectory(StatePath + ".tmp");

            var ex = Record.Exception(() =>
                w.WriteCheckpoint(complete: true, modelVersion: "v1", elementCount: 200, closed: false));

            Assert.Null(ex); // best-effort: a failed compaction must never fail the checkpoint
            Assert.False(File.Exists(StatePath)); // never compacted
            Assert.True(File.Exists(Journal(0))); // journal generation wasn't bumped — still current
        }

        [Fact]
        public void LeftoverTmpFile_DoesNotBreakNextSave()
        {
            Directory.CreateDirectory(Path.Combine(_root, "model-a"));
            File.WriteAllText(StatePath + ".tmp", "garbage from an interrupted save");

            var state = new ModelLogState { LastSeq = 7 };
            StateStore.Save(StatePath, state);

            Assert.Equal(7, StateStore.Load(StatePath).LastSeq);
        }

        [Fact]
        public void TornLastJournalLine_Ignored()
        {
            using (var w = new ModelLogWriter(_root, "model-a"))
            {
                w.WriteIfChanged(RecordKinds.El, "guid-1", El(1, 1, "W"));
                w.FlushState();
            }

            File.AppendAllText(Journal(0), "{\"op\":\"hs\",\"f\":\"el\",\"id\":\"guid-2\",\"h\":{");

            using var reopened = new ModelLogWriter(_root, "model-a");
            Assert.False(reopened.WriteIfChanged(RecordKinds.El, "guid-1", El(1, 1, "W"))); // survived
            Assert.True(reopened.WriteIfChanged(RecordKinds.El, "guid-2", El(2, 1, "W"))); // torn op never applied
        }

        [Fact]
        public void StaleJournalGeneration_Ignored()
        {
            using (var w = new ModelLogWriter(_root, "model-a"))
                w.WriteIfChanged(RecordKinds.El, "guid-1", El(1, 1, "W"));

            // Simulate a compaction interrupted after the new base was written but before the old
            // journal was deleted: a leftover gen-0 journal claiming a since-removed id, next to a
            // base already at gen 1 with no gen-1 journal.
            var state = StateStore.Load(StatePath);
            Assert.Equal(0, state.JournalGeneration); // no compaction actually happened yet here
            state.JournalGeneration = 1;
            StateStore.Save(StatePath, state);
            File.WriteAllText(Journal(0), "{\"op\":\"hr\",\"f\":\"el\",\"id\":\"guid-1\"}\n");

            using var reopened = new ModelLogWriter(_root, "model-a");
            Assert.False(reopened.WriteIfChanged(RecordKinds.El, "guid-1", El(1, 1, "W"))); // gen-0 op not replayed
            Assert.False(File.Exists(Journal(0))); // cleaned up opportunistically on open
        }

        [Fact]
        public void OldFormatStateJson_NoJournalGenerationNoJournal_LoadsFine()
        {
            Directory.CreateDirectory(Path.Combine(_root, "model-a"));
            File.WriteAllText(StatePath, "{\n  \"LastSeq\": 5,\n  \"CurrentSegment\": 1,\n  \"LastCheckpointClosed\": true\n}\n");

            using var w = new ModelLogWriter(_root, "model-a");
            Assert.True(w.LastCheckpointClosed);
            Assert.True(w.WriteIfChanged(RecordKinds.El, "guid-1", El(1, 1, "W"))); // never seen before
        }

        [Fact]
        public void ChangeBatch_ThatWritesNothing_LeavesLogUntouched()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.WriteIfChanged(RecordKinds.El, "guid-1", El(1, 1, "W"));
            var lines = File.ReadAllLines(Seg(1)).Length;

            w.BeginChange(new JsonObject { ["modified"] = 1 }, deleted: 0);
            w.WriteIfChanged(RecordKinds.El, "guid-1", El(1, 1, "W")); // unchanged
            w.EndChange();

            Assert.Equal(lines, File.ReadAllLines(Seg(1)).Length);
        }

        [Fact]
        public void ChangeBatch_ChgWrittenJustBeforeFirstRecord()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.WriteIfChanged(RecordKinds.El, "guid-1", El(1, 1, "W"));

            w.BeginChange(new JsonObject { ["modified"] = 1 }, deleted: 0);
            w.WriteIfChanged(RecordKinds.El, "guid-1", El(1, 2, "W"));
            w.EndChange();

            var kinds = File.ReadAllLines(Seg(1)).Select(l => JsonNode.Parse(l)!["k"]!.GetValue<string>()).ToList();
            Assert.Equal(new[] { "el", "chg", "el" }, kinds);
        }

        [Fact]
        public void ChangeBatch_WithDeletions_WritesChgRightAway()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.BeginChange(new JsonObject { ["deleted"] = 2 }, deleted: 2);
            w.EndChange();

            Assert.Single(File.ReadAllLines(Seg(1)), l => l.Contains("\"k\":\"chg\""));
        }

        [Fact]
        public void StateBehindLog_SeqRecoveredFromLog_NeverReused()
        {
            using (var w = new ModelLogWriter(_root, "model-a"))
                w.Append(RecordKinds.Project, new JsonObject { ["number"] = "1" }); // saved on Dispose: LastSeq 1

            // Lines flushed after the last state save, then Revit died before the next one.
            File.AppendAllText(Seg(1), "{\"seq\":2,\"ts\":\"x\",\"k\":\"el\",\"id\":\"a\"}\n");
            File.AppendAllText(Seg(1), "{\"seq\":3,\"ts\":\"x\",\"k\":\"el\",\"id\":\"b\"}\n");
            File.AppendAllText(Seg(1), "{\"seq\":4,\"ts\":\"x\",\"k\""); // torn last line

            using var reopened = new ModelLogWriter(_root, "model-a");
            Assert.Equal(3, reopened.LastSeq);
            Assert.Equal(4, reopened.Append(RecordKinds.Project, new JsonObject()));
        }

        [Fact]
        public void StateBehindLog_NewerSegmentOnDisk_IsReopened()
        {
            using (var w = new ModelLogWriter(_root, "model-a"))
                w.Append(RecordKinds.Project, new JsonObject()); // state says segment 1

            // A rotation happened after that save; its state was never persisted.
            File.WriteAllText(Seg(2), "{\"seq\":9,\"ts\":\"x\",\"k\":\"header\"}\n");

            using var reopened = new ModelLogWriter(_root, "model-a");
            Assert.Equal(10, reopened.Append(RecordKinds.Project, new JsonObject()));
            Assert.Equal(2, File.ReadAllLines(Seg(2)).Length);
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
        public void SecondWriter_SameModel_NeverTouchesSegmentFiles()
        {
            using var w1 = new ModelLogWriter(_root, "model-a");
            w1.Append(RecordKinds.Project, new JsonObject { ["number"] = "1" });
            var linesBefore = File.ReadAllLines(Seg(1)).Length;

            // A stray .gz.tmp the OWNER left mid-compression — the second writer's constructor
            // must not run LogSegmentWriter's startup recovery (which deletes/recompresses) on
            // files it doesn't own.
            var tmp = Path.Combine(_root, "model-a", "000001.jsonl.gz.tmp");
            File.WriteAllText(tmp, "owner's in-flight compression");

            var ex = Record.Exception(() =>
            {
                using var w2 = new ModelLogWriter(_root, "model-a");
                Assert.True(w2.LockHeldElsewhere);
                w2.Append(RecordKinds.Project, new JsonObject { ["number"] = "2" }); // must no-op
                w2.WriteCheckpoint(complete: true, modelVersion: "v1", elementCount: 1, closed: false); // must no-op
                Assert.False(w2.RotationDue);
            });

            Assert.Null(ex); // constructor/writes must not throw despite the owner's open handle
            Assert.True(File.Exists(tmp)); // the owner's in-flight file was left alone
            Assert.Equal(linesBefore, File.ReadAllLines(Seg(1)).Length); // second writer wrote nothing
        }

        [Fact]
        public void RotationDue_TrueOncePastThreshold()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            Assert.False(w.RotationDue);

            var padding = new string('x', 1024 * 1024); // ~1 MiB per record
            for (var i = 0; i < 65; i++) w.Append(RecordKinds.Project, new JsonObject { ["pad"] = padding });

            Assert.True(w.RotationDue); // past LogSegmentWriter.RotateAtBytes (64 MiB)
        }

        [Fact]
        public void RotationDue_FalseAfterBeginNewGenerationRotates()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            var padding = new string('x', 1024 * 1024);
            for (var i = 0; i < 65; i++) w.Append(RecordKinds.Project, new JsonObject { ["pad"] = padding });
            Assert.True(w.RotationDue);

            w.BeginNewGeneration(new JsonObject { ["title"] = "Test.rvt" });

            Assert.False(w.RotationDue); // the new segment starts empty
        }

        [Fact]
        public void BeginNewGeneration_RotatesWhenSegmentHasContent()
        {
            var w = new ModelLogWriter(_root, "model-a");
            w.Append(RecordKinds.Project, new JsonObject { ["number"] = "1" }); // gives segment 1 content
            w.BeginNewGeneration(new JsonObject { ["title"] = "Test.rvt" });
            w.Dispose(); // bounded wait for the background gzip the rotation above started

            Assert.True(File.Exists(Path.Combine(_root, "model-a", "000001.jsonl.gz"))); // old segment gzipped
            Assert.True(File.Exists(Seg(2)));
        }

        [Fact]
        public void BeginNewGeneration_EmptySegment_DoesNotRotate()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.BeginNewGeneration(new JsonObject { ["title"] = "Test.rvt" });

            Assert.True(File.Exists(Seg(1)));
            Assert.False(File.Exists(Seg(2)));
        }

        [Fact]
        public void BeginNewGeneration_FirstLineOfNewSegmentIsHeader()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            w.Append(RecordKinds.Project, new JsonObject { ["number"] = "1" });

            w.BeginNewGeneration(new JsonObject { ["title"] = "Test.rvt" });

            var firstLine = File.ReadAllLines(Seg(2)).First();
            var parsed = JsonNode.Parse(firstLine)!.AsObject();
            Assert.Equal("header", parsed["k"]!.GetValue<string>());
            Assert.Equal("Test.rvt", parsed["title"]!.GetValue<string>());
        }

        [Fact]
        public void BeginNewGeneration_PdefAndCatWrittenAgainAfterwards()
        {
            using var w = new ModelLogWriter(_root, "model-a");
            Assert.True(w.WriteIfUnseen(RecordKinds.Pdef, "builtin:FIRE_RATING", new JsonObject { ["name"] = "Fire Rating" }));
            Assert.True(w.WriteIfUnseen(RecordKinds.Cat, "c:Walls", new JsonObject { ["name"] = "Walls" }));
            Assert.False(w.WriteIfUnseen(RecordKinds.Pdef, "builtin:FIRE_RATING", new JsonObject { ["name"] = "Fire Rating" }));

            w.BeginNewGeneration(new JsonObject { ["title"] = "Test.rvt" });

            Assert.True(w.WriteIfUnseen(RecordKinds.Pdef, "builtin:FIRE_RATING", new JsonObject { ["name"] = "Fire Rating" }));
            Assert.True(w.WriteIfUnseen(RecordKinds.Cat, "c:Walls", new JsonObject { ["name"] = "Walls" }));
        }

        [Fact]
        public void BeginNewGeneration_ClearedSeenSets_StayClearedAfterReopen()
        {
            using (var w = new ModelLogWriter(_root, "model-a"))
            {
                w.WriteIfUnseen(RecordKinds.Pdef, "builtin:FIRE_RATING", new JsonObject { ["name"] = "Fire Rating" });
                w.BeginNewGeneration(new JsonObject { ["title"] = "Test.rvt" });
            }

            // A reload must not resurrect the cleared set — the compaction BeginNewGeneration
            // forces bumps the journal generation and drops the old journal, so the old "pd" seen
            // op (recorded under the previous generation) is never replayed on top of the new base.
            using var reopened = new ModelLogWriter(_root, "model-a");
            Assert.True(reopened.WriteIfUnseen(RecordKinds.Pdef, "builtin:FIRE_RATING", new JsonObject { ["name"] = "Fire Rating" }));
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

        private static readonly TimeSpan CompressionWait = TimeSpan.FromSeconds(5);

        [Fact]
        public void Rotate_NewSegmentWritableBeforeCompressionFinishes()
        {
            var dir = Path.Combine(_root, "async-rotate");
            using var seg = new LogSegmentWriter(dir, 1);
            seg.AppendLine("{\"seq\":1}");
            seg.Rotate(); // must not block on gzip — the new segment is already open here
            seg.AppendLine("{\"seq\":2}");

            Assert.True(seg.WaitForBackgroundCompression(CompressionWait));
            Assert.True(File.Exists(Path.Combine(dir, "000001.jsonl.gz")));
            Assert.False(File.Exists(Path.Combine(dir, "000001.jsonl")));
            Assert.Equal("{\"seq\":2}", File.ReadAllLines(Path.Combine(dir, "000002.jsonl")).Single());
        }

        [Fact]
        public void Recover_PlainSegmentWithNoGz_IsCompressedInBackground()
        {
            var dir = Path.Combine(_root, "recover-plain");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "000001.jsonl"), "{\"seq\":1}\n");

            using var seg = new LogSegmentWriter(dir, 2); // simulates resuming after a crash mid-compression

            Assert.True(seg.WaitForBackgroundCompression(CompressionWait));
            Assert.True(File.Exists(Path.Combine(dir, "000001.jsonl.gz")));
            Assert.False(File.Exists(Path.Combine(dir, "000001.jsonl")));
        }

        [Fact]
        public void Recover_StaleGzTmp_DeletedBeforeRecompressing()
        {
            var dir = Path.Combine(_root, "recover-tmp");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "000001.jsonl"), "{\"seq\":1}\n");
            File.WriteAllText(Path.Combine(dir, "000001.jsonl.gz.tmp"), "garbage from an interrupted compression");

            using var seg = new LogSegmentWriter(dir, 2);

            Assert.True(seg.WaitForBackgroundCompression(CompressionWait));
            Assert.False(File.Exists(Path.Combine(dir, "000001.jsonl.gz.tmp")));
            Assert.True(File.Exists(Path.Combine(dir, "000001.jsonl.gz")));
        }

        [Fact]
        public void Recover_PlainWithExistingGz_OnlyPlainDeleted_NoBackgroundWork()
        {
            var dir = Path.Combine(_root, "recover-both");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "000001.jsonl"), "{\"seq\":1}\n");
            File.WriteAllText(Path.Combine(dir, "000001.jsonl.gz"), "already compressed");

            using var seg = new LogSegmentWriter(dir, 2); // cleanup here is synchronous — no task to wait for

            Assert.False(File.Exists(Path.Combine(dir, "000001.jsonl")));
            Assert.Equal("already compressed", File.ReadAllText(Path.Combine(dir, "000001.jsonl.gz")));
        }
    }
}
