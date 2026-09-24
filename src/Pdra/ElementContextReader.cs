using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
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

        /// <summary>Element's type_id/type_name pair — the same GetTypeId()+doc.GetElement() lookup
        /// already duplicated across get_element_by_uniqueid, get_element_by_ifcguid and
        /// get_door_rooms, centralized so list_elements and filter_elements_by_scope_box can carry
        /// it too without a fourth copy. Returns (null, null) when the element has no distinct type
        /// (GetTypeId() invalid) — omit, don't blank.</summary>
        public static (long? TypeId, string? TypeName) ResolveType(Element el)
        {
            var typeId = el.GetTypeId();
            if (typeId == ElementId.InvalidElementId) return (null, null);
            var typeElem = el.Document.GetElement(typeId);
            return (typeId.Value, typeElem?.Name);
        }

        /// <summary>The ALL_MODEL_MARK built-in parameter — the human-facing tag ("D-104") that
        /// appears on drawings and in emails, already read this way in GetDoorRoomsTool. Null when
        /// unset (omit, don't blank).</summary>
        public static string? ResolveMark(Element el) =>
            el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();

        /// <summary>The element's design option as {id, name, is_primary}, or null when it lives in
        /// the main model (no design option) — was FilterElementsByScopeBoxTool-private; moved here
        /// so any element-returning tool can attach the same field instead of a second copy.</summary>
        public static JsonObject? ResolveDesignOption(Element el)
        {
            DesignOption? opt;
            try { opt = el.DesignOption; } catch { return null; }
            if (opt is null) return null;
            bool isPrimary = false;
            try { isPrimary = opt.IsPrimary; } catch { }
            return new JsonObject
            {
                ["id"]         = opt.Id.Value,
                ["name"]       = opt.Name,
                ["is_primary"] = isPrimary,
            };
        }

        /// <summary>Resolves the room enclosing an element's location point (or bbox-centroid
        /// fallback for a curve-based/no-location element) via Document.GetRoomAtPoint — the same
        /// geometric fallback GetDoorRoomsTool already uses for doors when From/To Room isn't set.
        /// Doors additionally have get_FromRoom/get_ToRoom (two rooms, door-specific semantics) —
        /// GetDoorRoomsTool keeps that logic itself; this is the general single-room case any OTHER
        /// element-returning tool (list_elements, filter_elements_by_scope_box) can use for "which
        /// room is this element in". Returns null when unresolvable (no phase, no enclosing room, or
        /// the API throws) — omit, don't blank.</summary>
        public static JsonObject? ResolveRoom(Element el, Phase? phase)
        {
            if (phase is null) return null;
            var doc = el.Document;

            var pt = (el.Location as LocationPoint)?.Point;
            if (pt is null)
            {
                BoundingBoxXYZ? bb;
                try { bb = el.get_BoundingBox(null); } catch { bb = null; }
                if (bb is null) return null;
                pt = (bb.Min + bb.Max).Multiply(0.5);
            }

            Room? room;
            try { room = doc.GetRoomAtPoint(pt, phase) as Room; } catch { return null; }
            if (room is null) return null;

            return new JsonObject
            {
                ["id"]         = room.Id.Value,
                ["name"]       = room.Name,
                ["number"]     = room.Number,
                ["level_name"] = (doc.GetElement(room.LevelId) as Level)?.Name,
            };
        }

        /// <summary>Lean variant of <see cref="ResolveRoom"/> for the model-log path (
        /// <c>RecordBuilder.BuildLocationRef</c>), which only ever uses the room's id — never its
        /// name/number/level, which <see cref="ResolveRoom"/> reads just to discard here. Also
        /// takes the element's bounding box from the caller instead of computing its own (the
        /// model-log path already computes one bbox per element for <c>BuildGeometryRef</c>).
        /// Same phase/point/GetRoomAtPoint logic as <see cref="ResolveRoom"/>, kept as a separate
        /// method so ResolveRoom's own output (used by the frozen MCP tools) never changes.</summary>
        public static ElementId? ResolveRoomId(Element el, Phase? phase, BoundingBoxXYZ? bbox)
        {
            if (phase is null) return null;

            var pt = (el.Location as LocationPoint)?.Point;
            if (pt is null)
            {
                if (bbox is null) return null;
                pt = (bbox.Min + bbox.Max).Multiply(0.5);
            }

            Room? room;
            try { room = el.Document.GetRoomAtPoint(pt, phase) as Room; } catch { return null; }
            return room?.Id;
        }

        /// <summary>Best-effort default Phase for room resolution when the caller has none of its
        /// own — the active view's phase, else the document's last phase, else null. Mirrors
        /// GetDoorRoomsTool.ResolvePhase's own default-selection fallback so a second caller doesn't
        /// duplicate that logic to get "a reasonable phase".</summary>
        public static Phase? DefaultPhase(Document doc, Autodesk.Revit.UI.UIDocument? uidoc)
        {
            try
            {
                var vp = uidoc?.ActiveView?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                if (vp is { } id && id != ElementId.InvalidElementId && doc.GetElement(id) is Phase vph) return vph;
            }
            catch { }
            try
            {
                var phases = doc.Phases;
                return phases.Size > 0 ? phases.get_Item(phases.Size - 1) : null;
            }
            catch { }
            return null;
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

        /// <summary>Reads a named parameter into a typed, machine-usable shape instead of a bare
        /// <see cref="Parameter.AsValueString"/> string nothing downstream can safely parse (e.g.
        /// "3.2 m" — is that meters, is it even a number). Returns null when the element carries no
        /// such parameter (omit, don't blank). See <see cref="ReadParamTyped(Parameter)"/> for the
        /// shape.</summary>
        public static JsonObject? ReadParamTyped(Element? el, string name)
        {
            var p = el?.LookupParameter(name);
            return p is null ? null : ReadParamTyped(p);
        }

        /// <summary>Same read as <see cref="ReadParamTyped(Element?, string)"/>, for a <see
        /// cref="Parameter"/> already in hand (avoids a second by-name lookup — used by
        /// pdra_get_element_parameters while it walks Element.Parameters). Shape: storage_type
        /// (String/Integer/Double/ElementId/None), has_value, value (StorageType-typed — a real
        /// number for Double/Integer, the raw ElementId for ElementId, never a formatted string for
        /// those), unit (present only for Double storage with a recognised unit — Revit's own
        /// INTERNAL unit, e.g. feet for length, radians for angle, unconverted from AsDouble() — so a
        /// caller converts deterministically instead of parsing AsValueString's locale-formatted
        /// text), and display (AsValueString(), the human formatting, when non-empty).</summary>
        public static JsonObject ReadParamTyped(Parameter p)
        {
            var node = new JsonObject
            {
                ["storage_type"] = p.StorageType.ToString(),
                ["has_value"]    = p.HasValue,
            };

            switch (p.StorageType)
            {
                case StorageType.String:
                    node["value"] = p.AsString();
                    break;
                case StorageType.Integer:
                    node["value"] = p.AsInteger();
                    break;
                case StorageType.Double:
                    node["value"] = p.AsDouble();
                    var unit = InternalUnitLabel(p);
                    if (unit is not null) node["unit"] = unit;
                    break;
                case StorageType.ElementId:
                    var id = p.AsElementId();
                    node["value"] = id != ElementId.InvalidElementId ? id.Value : null;
                    break;
                default:
                    node["value"] = null;
                    break;
            }

            var display = p.AsValueString();
            if (!string.IsNullOrEmpty(display)) node["display"] = display;

            return node;
        }

        /// <summary>Best-effort Revit-internal-unit label for a Double-storage parameter's raw
        /// AsDouble() value, via the parameter's SPEC (<c>Definition.GetDataType()</c> — Revit
        /// 2021+, present on both this connector's targets, net48/Revit 2024 and
        /// net8.0-windows/Revit 2025-26; <c>Parameter.GetUnitTypeId()</c> returns the UNIT, e.g.
        /// millimeters, never equal to a `SpecTypeId.*` constant, which silently made every
        /// comparison below false). Only the common specs are named (Revit's own documented
        /// internal units: feet for length, radians for angle, …); anything else — or any throw
        /// (a unitless Double parameter has no spec at all) — returns null rather than
        /// guessing.</summary>
        private static string? InternalUnitLabel(Parameter p)
        {
            try
            {
                var specId = p.Definition?.GetDataType();
                if (specId is null || specId.Empty()) return null;
                if (specId == SpecTypeId.Length) return "ft";
                if (specId == SpecTypeId.Area) return "ft2";
                if (specId == SpecTypeId.Volume) return "ft3";
                if (specId == SpecTypeId.Angle) return "rad";
                if (specId == SpecTypeId.HvacTemperature) return "F";
                return null;
            }
            catch { return null; }
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
