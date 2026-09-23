using System;
using System.Collections.Generic;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// Per-model cache of "last hash written per field-group, per id, per family" (family =
    /// el/type/node/grid/mat/sheet/rev/link — every record kind whose rows are re-checked on
    /// every reconcile), plus write-once sets for definitions (pdef/cat) that are written the
    /// first time they're seen and never re-checked. Persisted as part of <see
    /// cref="ModelLogState"/> (state.json); serialized as plain <see cref="Dictionary{TKey,TValue}"/>
    /// trees so <c>System.Text.Json</c>'s reflection-based (de)serializer needs nothing special.
    ///
    /// This is what makes every trigger in docs/MODEL_LOG.md's "When the connector writes" table
    /// cheap — a handful of hash compares, not a full re-serialize-and-diff — and what makes the
    /// log self-healing: a reconcile only ever writes what actually changed since the last write,
    /// from ANY source (this connector's own last session, or another user's sync).
    /// </summary>
    public sealed class HashCache
    {
        /// family -> id -> field-group name -> hash of that field-group's last-written value.
        public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Families { get; set; }
            = new(StringComparer.Ordinal);

        public HashSet<string> PdefSeen { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> CatSeen { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, string>? Get(string family, string id) =>
            Families.TryGetValue(family, out var m) && m.TryGetValue(id, out var f) ? f : null;

        public void Set(string family, string id, Dictionary<string, string> fieldHashes)
        {
            if (!Families.TryGetValue(family, out var m))
                Families[family] = m = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            m[id] = fieldHashes;
        }

        public bool Remove(string family, string id) =>
            Families.TryGetValue(family, out var m) && m.Remove(id);

        public IEnumerable<string> KnownIds(string family) =>
            Families.TryGetValue(family, out var m) ? new List<string>(m.Keys) : (IEnumerable<string>)Array.Empty<string>();
    }
}
