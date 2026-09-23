using Loam.Revit.Connector.ModelLog;
using System;
using System.IO;
using Xunit;

namespace ModelLog.Tests
{
    public class StateStoreTests : IDisposable
    {
        private readonly string _dir;

        public StateStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "state-store-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void Load_MissingFile_ReturnsFreshState()
        {
            var state = StateStore.Load(Path.Combine(_dir, "state.json"));
            Assert.Equal(0, state.LastSeq);
            Assert.False(state.LastCheckpointClosed);
        }

        [Fact]
        public void SaveThenLoad_RoundTripsHashCache()
        {
            var path = Path.Combine(_dir, "state.json");
            var state = new ModelLogState { LastSeq = 42, LastCheckpointClosed = true };
            state.Cache.Set(RecordKinds.El, "guid-1", new System.Collections.Generic.Dictionary<string, string>
            {
                ["h"] = "abc123",
            });
            state.Cache.PdefSeen.Add("builtin:FIRE_RATING");

            StateStore.Save(path, state);
            var reloaded = StateStore.Load(path);

            Assert.Equal(42, reloaded.LastSeq);
            Assert.True(reloaded.LastCheckpointClosed);
            Assert.Equal("abc123", reloaded.Cache.Get(RecordKinds.El, "guid-1")!["h"]);
            Assert.Contains("builtin:FIRE_RATING", reloaded.Cache.PdefSeen);
        }

        [Fact]
        public void Load_CorruptFile_ReturnsFreshStateInsteadOfThrowing()
        {
            var path = Path.Combine(_dir, "state.json");
            File.WriteAllText(path, "{ not valid json");

            var state = StateStore.Load(path);
            Assert.Equal(0, state.LastSeq);
        }

        [Fact]
        public void Save_LeavesNoTempFileBehind()
        {
            var path = Path.Combine(_dir, "state.json");
            StateStore.Save(path, new ModelLogState { LastSeq = 1 });

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
    }
}
