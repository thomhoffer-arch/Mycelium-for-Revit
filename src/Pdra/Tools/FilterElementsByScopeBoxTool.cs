using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Membership test: which of the given elements fall inside a named scope box —
    /// the deterministic primitive for a zone resolver (e.g. "Zone B = everything in
    /// scope box X"). Honours the scope box's own orientation (its bbox Transform),
    /// so a rotated box is tested correctly, not by its axis-aligned world envelope.
    /// </summary>
    public sealed class FilterElementsByScopeBoxTool : IPdraTool
    {
        public string Name        => "pdra_filter_elements_by_scope_box";
        public string Description =>
            "Test which elements fall inside a scope box — the zone-membership primitive (e.g. 'Zone B = " +
            "everything in scope box X'). Identify the box by scope_box_id or scope_box_name. mode: 'centroid' " +
            "(default — location/centroid inside the box) or 'intersects' (element bbox overlaps the box). The " +
            "box's own rotation is respected. Each row carries {id, name, category, in_box, " +
            "design_option{name,is_primary}|null, level|null, from_link, project} so a zone resolver filters on " +
            "real data (primary-option / arch-levels / project), plus a summary {count_in, count_out}; set " +
            "inside_only=true to return only the members.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["scope_box_id"]   = new JsonObject { ["type"] = "integer", ["description"] = "Scope box element id (OST_VolumeOfInterest)." },
                ["scope_box_name"] = new JsonObject { ["type"] = "string", ["description"] = "Scope box name (alternative to scope_box_id)." },
                ["element_ids"]    = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "Elements to test. Omit with category to use the current selection." },
                ["category"]       = new JsonObject { ["type"] = "string", ["description"] = "BuiltInCategory to test in bulk, e.g. OST_Doors." },
                ["view_id"]        = new JsonObject { ["type"] = "integer", ["description"] = "Limit a category query to this view." },
                ["mode"]           = new JsonObject { ["type"] = "string", ["description"] = "'centroid' (default) or 'intersects'." },
                ["inside_only"]    = new JsonObject { ["type"] = "boolean", ["description"] = "Return only elements inside the box. Default false (all, each with in_box)." },
                ["limit"]          = new JsonObject { ["type"] = "integer", ["description"] = "Max elements to test. Default 1000." },
            },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var uidoc = ctx.UiApp.ActiveUIDocument;
            var doc   = uidoc?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            // Resolve the scope box.
            Element? sb = null;
            if (args.TryGetLong("scope_box_id", out var sbid)) sb = doc.GetElement(new ElementId(sbid));
            else if (args.TryGetString("scope_box_name", out var sbn))
                sb = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                    .FirstOrDefault(e => string.Equals(e.Name, sbn, StringComparison.OrdinalIgnoreCase));

            if (sb is null) return ToolResult.Error("Provide scope_box_id or scope_box_name that resolves to a scope box.");
            if (sb.Category?.Id.Value != (long)BuiltInCategory.OST_VolumeOfInterest)
                return ToolResult.Error($"Element {sb.Id.Value} is not a scope box (OST_VolumeOfInterest).");

            var box = sb.get_BoundingBox(null);
            if (box is null) return ToolResult.Error($"Scope box {sb.Id.Value} has no bounding box.");

            bool intersects = args.TryGetString("mode", out var mode) &&
                              string.Equals(mode, "intersects", StringComparison.OrdinalIgnoreCase);
            bool insideOnly = args.TryGetProperty("inside_only", out var ioEl) && ioEl.ValueKind == JsonValueKind.True;

            int limit = 1000;
            if (args.TryGetProperty("limit", out var limEl) && limEl.ValueKind == JsonValueKind.Number)
                limit = JsonHelpers.Clamp(limEl.GetInt32(), 1, 10000);

            var elements = ResolveElements(uidoc, doc, args, out var targErr);
            if (targErr is not null) return ToolResult.Error(targErr);

            var inv = box.Transform?.Inverse;  // world → box-local (handles rotation)
            var rows = new JsonArray();
            int inCount = 0, tested = 0;

            foreach (var el in elements)
            {
                if (tested >= limit) break;
                tested++;

                bool hit = intersects ? IntersectsBox(el, box, inv) : CentroidInBox(el, box, inv);
                if (hit) inCount++;
                if (insideOnly && !hit) continue;

                var row = new JsonObject
                {
                    ["id"]       = el.Id.Value,
                    ["name"]     = el.Name,
                    ["category"] = el.Category?.Name,
                    ["in_box"]   = hit,
                };

                // Provenance / scoping fields so a zone resolver filters on real model
                // data (primary-option / arch-levels / project) instead of heuristics.
                row["design_option"] = DesignOptionNode(el);
                row["level"]         = ElementContextReader.ResolveLevel(el);
                row["from_link"]     = el.Document.IsLinked;
                row["project"]       = el.Document.Title;

                rows.Add(row);
            }

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject
            {
                ["scope_box"] = new JsonObject { ["id"] = sb.Id.Value, ["name"] = sb.Name },
                ["mode"]      = intersects ? "intersects" : "centroid",
                ["count_in"]  = inCount,
                ["count_out"] = tested - inCount,
                ["elements"]  = rows,
            }));
        }

        /// <summary>The element's design option as {id, name, is_primary}, or null when it
        /// lives in the main model — lets the caller keep only main-model + primary-option
        /// elements without guessing from names.</summary>
        private static JsonNode? DesignOptionNode(Element el)
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

        private static XYZ ToLocal(XYZ p, Transform? inv) => inv is not null ? inv.OfPoint(p) : p;

        private static bool PointInBox(XYZ pLocal, BoundingBoxXYZ box) =>
            pLocal.X >= box.Min.X && pLocal.X <= box.Max.X &&
            pLocal.Y >= box.Min.Y && pLocal.Y <= box.Max.Y &&
            pLocal.Z >= box.Min.Z && pLocal.Z <= box.Max.Z;

        private static bool CentroidInBox(Element el, BoundingBoxXYZ box, Transform? inv)
        {
            var p = (el.Location as LocationPoint)?.Point;
            if (p is null)
            {
                var bb = el.get_BoundingBox(null);
                if (bb is null) return false;
                p = (bb.Min + bb.Max).Multiply(0.5);
            }
            return PointInBox(ToLocal(p, inv), box);
        }

        /// <summary>True if the element's world bbox (sampled at its 8 corners, mapped into
        /// box-local space) has any corner inside the box, or its centroid is inside —
        /// a conservative overlap test that respects the box's rotation.</summary>
        private static bool IntersectsBox(Element el, BoundingBoxXYZ box, Transform? inv)
        {
            var bb = el.get_BoundingBox(null);
            if (bb is null) return false;

            foreach (var corner in Corners(bb))
                if (PointInBox(ToLocal(corner, inv), box)) return true;

            return PointInBox(ToLocal((bb.Min + bb.Max).Multiply(0.5), inv), box);
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
        {
            var mn = bb.Min; var mx = bb.Max;
            yield return new XYZ(mn.X, mn.Y, mn.Z);
            yield return new XYZ(mx.X, mn.Y, mn.Z);
            yield return new XYZ(mn.X, mx.Y, mn.Z);
            yield return new XYZ(mx.X, mx.Y, mn.Z);
            yield return new XYZ(mn.X, mn.Y, mx.Z);
            yield return new XYZ(mx.X, mn.Y, mx.Z);
            yield return new XYZ(mn.X, mx.Y, mx.Z);
            yield return new XYZ(mx.X, mx.Y, mx.Z);
        }

        private static IEnumerable<Element> ResolveElements(
            Autodesk.Revit.UI.UIDocument? uidoc, Document doc, JsonElement args, out string? err)
        {
            err = null;
            if (args.TryGetProperty("element_ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                return idsEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number)
                    .Select(e => doc.GetElement(new ElementId(e.GetInt64()))).Where(e => e is not null)!;

            View? scopeView = null;
            if (args.TryGetLong("view_id", out var vid)) scopeView = doc.GetElement(new ElementId(vid)) as View;

            if (args.TryGetString("category", out var catName))
            {
                if (!Enum.TryParse<BuiltInCategory>(catName, out var bic)) { err = $"Unknown BuiltInCategory '{catName}'."; return Enumerable.Empty<Element>(); }
                return (scopeView is not null
                        ? new FilteredElementCollector(doc, scopeView.Id)
                        : new FilteredElementCollector(doc))
                    .OfCategory(bic).WhereElementIsNotElementType().Cast<Element>();
            }

            var sel = uidoc?.Selection.GetElementIds();
            if (sel is { Count: > 0 })
                return sel.Select(id => doc.GetElement(id)).Where(e => e is not null)!;

            err = "Provide element_ids[], category, or a current selection to test.";
            return Enumerable.Empty<Element>();
        }
    }
}
