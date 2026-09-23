using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// One of the model-log handoff plan's "on-demand detail" tools — answers what the log
    /// can't answer cheaply from a static snapshot: spatial and structured queries evaluated
    /// live, using Revit's own geometry filters (<see cref="BoundingBoxIntersectsFilter"/>,
    /// <see cref="ElementIntersectsElementFilter"/>) rather than anything this connector would
    /// have to maintain a spatial index for itself.
    /// </summary>
    public sealed class FindElementsTool : IPdraTool
    {
        public string Name => "pdra_find_elements";
        public string Description =>
            "Element ids matching a spatial or structured query, evaluated live against the " +
            "current model (never the log). Exactly one of: `box` (inside/intersecting an axis-" +
            "aligned box, internal feet — e.g. \"what's in this region\"), `near` (within " +
            "`distance` feet of a given element's bounding box — e.g. \"what's within 1m of this " +
            "column\"), `intersects` (elements whose geometry intersects a given element — e.g. " +
            "\"what intersects this duct\"), or `param` (category + parameter name/value match). " +
            "Optionally narrow any of these with `category`. Returns unique_ids, paged.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        private const int MaxIdsPerCall = 5000;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["box"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["min"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" } },
                        ["max"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" } },
                    },
                    ["description"] = "Axis-aligned box [x,y,z] corners, internal feet.",
                },
                ["near"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string", ["description"] = "UniqueId of the reference element." },
                        ["distance"] = new JsonObject { ["type"] = "number", ["description"] = "Feet." },
                    },
                    ["required"] = new JsonArray { "id", "distance" },
                },
                ["intersects"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string", ["description"] = "UniqueId of the reference element." },
                    },
                    ["required"] = new JsonArray { "id" },
                },
                ["param"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject { ["type"] = "string" },
                        ["equals"] = new JsonObject { ["type"] = "string", ["description"] = "Matched against the parameter's display value (AsValueString)." },
                    },
                    ["required"] = new JsonArray { "name", "equals" },
                },
                ["category"] = new JsonObject { ["type"] = "string", ["description"] = "Narrow to this category (enum name or display name) — required for `param`." },
                ["limit"] = JsonHelpers.LimitSchemaProp(def: 500, max: MaxIdsPerCall),
                ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Skip this many matches (0-based) for paging." },
            },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            // Declared with an explicit initializer (not `out var` inline in the `&&`) — the
            // compiler's definite-assignment analysis doesn't carry "assigned when hasBox is
            // true" across into the unrelated `if (hasBox)` below (that narrowing only works
            // when the pattern variable is used directly in the SAME condition), so an inline
            // `out var` here left boxEl/nearEl/etc. "possibly unassigned" at their use sites.
            JsonElement boxEl = default, nearEl = default, intersectsEl = default, paramEl = default;
            var hasBox = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("box", out boxEl);
            var hasNear = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("near", out nearEl);
            var hasIntersects = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("intersects", out intersectsEl);
            var hasParam = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("param", out paramEl);

            var modeCount = (hasBox ? 1 : 0) + (hasNear ? 1 : 0) + (hasIntersects ? 1 : 0) + (hasParam ? 1 : 0);
            if (modeCount != 1)
                return ToolResult.Error("Exactly one of box, near, intersects, param is required.");

            BuiltInCategory? category = null;
            if (args.TryGetString("category", out var catName))
            {
                if (!CategoryResolver.TryResolve(doc, catName, out var bic, out var catErr))
                    return ToolResult.Error(catErr!);
                category = bic;
            }

            IEnumerable<ElementId> matchIds;
            try
            {
                if (hasBox) matchIds = FindInBox(doc, boxEl, category);
                else if (hasNear) matchIds = FindNear(doc, nearEl, category);
                else if (hasIntersects) matchIds = FindIntersecting(doc, intersectsEl, category);
                else matchIds = FindByParam(doc, paramEl, category);
            }
            catch (Exception ex)
            {
                return ToolResult.Error("Query failed: " + ex.Message);
            }

            var limit = args.GetLimit(def: 500, max: MaxIdsPerCall);
            var offset = args.TryGetInt("offset", out var o) ? Math.Max(0, o) : 0;

            var all = matchIds.ToList();
            var page = all.Skip(offset).Take(limit).ToList();

            var rows = new JsonArray();
            foreach (var id in page)
            {
                var el = doc.GetElement(id);
                if (el is null) continue;
                rows.Add(el.UniqueId);
            }

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject
            {
                ["total"] = all.Count,
                ["count"] = rows.Count,
                ["truncated"] = offset + rows.Count < all.Count,
                ["offset"] = offset,
                ["ids"] = rows,
            }));
        }

        private static IEnumerable<ElementId> FindInBox(Document doc, JsonElement boxEl, BuiltInCategory? category)
        {
            var min = ReadPoint(boxEl, "min");
            var max = ReadPoint(boxEl, "max");
            if (min is null || max is null) throw new ArgumentException("box.min and box.max are both required, each a 3-number array.");

            var outline = new Outline(min, max);
            var filter = new BoundingBoxIntersectsFilter(outline);
            var collector = category is { } bic
                ? new FilteredElementCollector(doc).OfCategory(bic)
                : new FilteredElementCollector(doc);
            return collector.WherePasses(filter).WhereElementIsNotElementType().ToElementIds();
        }

        private static IEnumerable<ElementId> FindNear(Document doc, JsonElement nearEl, BuiltInCategory? category)
        {
            if (!nearEl.TryGetString("id", out var refUid)) throw new ArgumentException("near.id is required.");
            if (!nearEl.TryGetDouble("distance", out var distance)) throw new ArgumentException("near.distance is required.");

            var refEl = doc.GetElement(refUid) ?? throw new ArgumentException($"Unknown element unique_id '{refUid}'.");
            var bb = refEl.get_BoundingBox(null) ?? throw new ArgumentException("Reference element has no bounding box.");

            var min = new XYZ(bb.Min.X - distance, bb.Min.Y - distance, bb.Min.Z - distance);
            var max = new XYZ(bb.Max.X + distance, bb.Max.Y + distance, bb.Max.Z + distance);
            var outline = new Outline(min, max);
            var filter = new BoundingBoxIntersectsFilter(outline);
            var collector = category is { } bic
                ? new FilteredElementCollector(doc).OfCategory(bic)
                : new FilteredElementCollector(doc);
            return collector.WherePasses(filter).WhereElementIsNotElementType()
                .Where(e => e.Id != refEl.Id).Select(e => e.Id);
        }

        private static IEnumerable<ElementId> FindIntersecting(Document doc, JsonElement intersectsEl, BuiltInCategory? category)
        {
            if (!intersectsEl.TryGetString("id", out var refUid)) throw new ArgumentException("intersects.id is required.");
            var refEl = doc.GetElement(refUid) ?? throw new ArgumentException($"Unknown element unique_id '{refUid}'.");

            var filter = new ElementIntersectsElementFilter(refEl);
            var collector = category is { } bic
                ? new FilteredElementCollector(doc).OfCategory(bic)
                : new FilteredElementCollector(doc);
            return collector.WherePasses(filter).WhereElementIsNotElementType()
                .Where(e => e.Id != refEl.Id).Select(e => e.Id);
        }

        // NOT an iterator method (no `yield return`) — a caller-facing ArgumentException must
        // throw the moment this is called, inside Run's try/catch, not on first enumeration of
        // a lazily-built sequence (which would happen outside that try/catch and surface as an
        // unhandled 500 instead of the same ToolResult.Error every sibling FindXxx returns).
        private static IEnumerable<ElementId> FindByParam(Document doc, JsonElement paramEl, BuiltInCategory? category)
        {
            if (category is null) throw new ArgumentException("category is required for a param query.");
            if (!paramEl.TryGetString("name", out var name)) throw new ArgumentException("param.name is required.");
            if (!paramEl.TryGetString("equals", out var expected)) throw new ArgumentException("param.equals is required.");

            var collector = new FilteredElementCollector(doc).OfCategory(category.Value).WhereElementIsNotElementType();
            var matches = new List<ElementId>();
            foreach (var el in collector)
            {
                var v = ElementContextReader.ReadParamValue(el, name);
                if (string.Equals(v, expected, StringComparison.OrdinalIgnoreCase)) matches.Add(el.Id);
            }
            return matches;
        }

        private static XYZ? ReadPoint(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var vals = arr.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            return vals.Length == 3 ? new XYZ(vals[0], vals[1], vals[2]) : null;
        }
    }
}
