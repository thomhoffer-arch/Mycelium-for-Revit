using Autodesk.Revit.DB;
using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Shared readers for an element's storey (Level) and classification
    /// (assembly / OmniClass). Derives the document and type from the element
    /// itself, so it works for host elements AND elements resolved inside a
    /// linked model (the element's own document is used throughout).
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
        /// instance (both from the element's own document). Returns null when none are
        /// populated (omit, don't blank).</summary>
        public static JsonObject? ResolveClassification(Element el)
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
            return cls;
        }

        private static BuiltInParameter[] ResolveBips(params string[] names)
            => names.Select(n => Enum.TryParse<BuiltInParameter>(n, out var b) ? b : BuiltInParameter.INVALID)
                    .Where(b => b != BuiltInParameter.INVALID).ToArray();

        private static (string, BuiltInParameter)[] ResolveLabelled(params (string Label, string Bip)[] pairs)
            => pairs.Where(p => Enum.TryParse<BuiltInParameter>(p.Bip, out _))
                    .Select(p => (p.Label, (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), p.Bip))).ToArray();
    }
}
