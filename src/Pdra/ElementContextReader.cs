using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Shared readers for an element's storey (Level) and classification
    /// (assembly / OmniClass, plus any caller-named parameter). Derives the
    /// document and type from the element itself, so it works for host elements
    /// AND elements resolved inside a linked model (the element's own document
    /// is used throughout).
    /// </summary>
    internal static class ElementContextReader
    {
        // Built-in params that point at the element's associated Level, tried in order
        // when Element.LevelId is unset. Resolved by name so a BIP missing from a given
        // Revit version's enum simply drops out instead of failing to compile.
        private static readonly BuiltInParameter[] LevelParams = ResolveBips(
            "WALL_BASE_CONSTRAINT", "FAMILY_LEVEL_PARAM", "FAMILY_BASE_LEVEL_PARAM",
            "SCHEDULE_LEVEL_PARAM", "INSTANCE_REFERENCE_LEVEL_PARAM",
            "INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM", "ROOM_LEVEL_ID", "LEVEL_PARAM",
            "STAIRS_BASE_LEVEL_PARAM", "GROUP_LEVEL");

        // Classification params (mostly type-level), label → BIP.
        private static readonly (string Label, BuiltInParameter Bip)[] ClassificationParams =
            ResolveLabelled(
                ("assembly_code",        "UNIFORMAT_CODE"),
                ("assembly_description", "UNIFORMAT_DESCRIPTION"),
                ("omniclass_code",       "OMNICLASS_CODE"),
                ("omniclass_description","OMNICLASS_DESCRIPTION"));

        /// <summary>Element → its associated Level (from the element's own document),
        /// via Element.LevelId then a fallback scan of level-bearing params. Returns
        /// null when the element has no level.</summary>
        public static JsonObject? ResolveLevel(Element el)
        {
            var doc = el.Document;

            ElementId lvlId;
            try { lvlId = el.LevelId; } catch { lvlId = ElementId.InvalidElementId; }

            if (lvlId == ElementId.InvalidElementId)
            {
                foreach (var bip in LevelParams)
                {
                    var p = el.get_Parameter(bip);
                    if (p is null || p.StorageType != StorageType.ElementId) continue;
                    var id = p.AsElementId();
                    if (id != ElementId.InvalidElementId && doc.GetElement(id) is Level) { lvlId = id; break; }
                }
            }

            if (lvlId == ElementId.InvalidElementId || doc.GetElement(lvlId) is not Level lvl) return null;

            return new JsonObject
            {
                ["id"]                   = lvl.Id.Value,
                ["name"]                 = lvl.Name,
                ["elevation_ft"]         = lvl.Elevation,
                ["elevation_user_units"] = lvl.get_Parameter(BuiltInParameter.LEVEL_ELEV)?.AsValueString(),
            };
        }

        /// <summary>Assembly/OmniClass codes, read off the element's type then the
        /// instance (both from the element's own document), plus any caller-named
        /// <paramref name="extraParams"/> (e.g. an office's NL-SfB/Uniclass shared
        /// parameter — see pdra_get_classification_sources for discovering the name).
        /// Extra params are looked up the same way (type first, then instance) and
        /// merged in under their own name. Returns null when nothing is populated
        /// (omit, don't blank).</summary>
        public static JsonObject? ResolveClassification(Element el, IReadOnlyList<string>? extraParams = null)
        {
            var typeId   = el.GetTypeId();
            var typeElem = typeId != ElementId.InvalidElementId ? el.Document.GetElement(typeId) : null;

            JsonObject? cls = null;
            foreach (var (label, bip) in ClassificationParams)
            {
                var v = typeElem?.get_Parameter(bip)?.AsString();
                if (string.IsNullOrEmpty(v)) v = el.get_Parameter(bip)?.AsString();
                if (string.IsNullOrEmpty(v)) continue;
                cls ??= new JsonObject();
                cls[label] = v;
            }

            if (extraParams is not null)
            {
                foreach (var name in extraParams)
                {
                    var v = ReadParamValue(typeElem, name) ?? ReadParamValue(el, name);
                    if (v is null) continue;
                    cls ??= new JsonObject();
                    cls[name] = v;
                }
            }

            return cls;
        }

        /// <summary>Reads a named parameter's display value off an element — <see
        /// cref="Parameter.AsValueString"/> first (honours the parameter's own units/
        /// formatting), falling back to a StorageType-appropriate raw read. Shared by
        /// classification_params lookups and pdra_get_door_rooms' door/room parameter
        /// reads (door_params/room_params).</summary>
        public static string? ReadParamValue(Element? el, string name)
        {
            var p = el?.LookupParameter(name);
            return p is null ? null : ReadParamValue(p);
        }

        /// <summary>Same coercion as <see cref="ReadParamValue(Element?, string)"/>, for a
        /// <see cref="Parameter"/> already in hand (avoids a second by-name lookup —
        /// used by pdra_get_classification_sources while it walks Element.Parameters).</summary>
        public static string? ReadParamValue(Parameter p)
        {
            var vs = p.AsValueString();
            if (!string.IsNullOrEmpty(vs)) return vs;
            return p.StorageType switch
            {
                StorageType.String    => p.AsString(),
                StorageType.Integer   => p.AsInteger().ToString(),
                StorageType.Double    => p.AsDouble().ToString("0.######"),
                StorageType.ElementId => p.AsElementId().Value.ToString(),
                _                     => null,
            };
        }

        private static BuiltInParameter[] ResolveBips(params string[] names)
            => names.Select(n => Enum.TryParse<BuiltInParameter>(n, out var b) ? b : BuiltInParameter.INVALID)
                    .Where(b => b != BuiltInParameter.INVALID).ToArray();

        private static (string, BuiltInParameter)[] ResolveLabelled(params (string Label, string Bip)[] pairs)
            => pairs.Where(p => Enum.TryParse<BuiltInParameter>(p.Bip, out _))
                    .Select(p => (p.Label, (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), p.Bip))).ToArray();

        /// <summary>
        /// Accumulates, across the rows a tool actually returns, which classification
        /// labels were probed (the built-ins always; caller-supplied classification_params
        /// on top) and how many rows had each populated — the classification_sources
        /// response envelope. Presence of this envelope on a response is what marks the
        /// field as supported; a label's populated count staying 0 means the elements
        /// returned genuinely carry no value for it — the two states Loam's report asked
        /// to be able to tell apart, without ever writing classification: null onto a row
        /// (SpineKeys' "omit, never blank" rule stays intact).
        /// </summary>
        public sealed class ClassificationEnvelope
        {
            private readonly List<(string Label, string? Parameter, string? Builtin)> _probed = new();
            private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

            internal ClassificationEnvelope(IReadOnlyList<string>? extraParams)
            {
                foreach (var (label, bip) in ClassificationParams)
                    _probed.Add((label, DisplayName(bip), bip.ToString()));
                if (extraParams is not null)
                    foreach (var name in extraParams)
                        _probed.Add((name, name, null));
            }

            /// <summary>Call once per row after computing its classification (or null).</summary>
            public void Record(JsonObject? cls)
            {
                if (cls is null) return;
                foreach (var kv in cls)
                    _counts[kv.Key] = _counts.TryGetValue(kv.Key, out var n) ? n + 1 : 1;
            }

            public JsonObject Build()
            {
                var arr = new JsonArray();
                foreach (var (label, parameter, builtin) in _probed)
                {
                    arr.Add(new JsonObject
                    {
                        ["label"]     = label,
                        ["parameter"] = parameter,
                        ["builtin"]   = builtin,
                        ["populated"] = _counts.TryGetValue(label, out var n) ? n : 0,
                    });
                }
                return new JsonObject { ["supported"] = true, ["probed"] = arr };
            }

            private static string? DisplayName(BuiltInParameter bip)
            {
                try { return LabelUtils.GetLabelFor(bip); } catch { return bip.ToString(); }
            }
        }

        public static ClassificationEnvelope NewClassificationEnvelope(IReadOnlyList<string>? extraParams) => new(extraParams);
    }
}
