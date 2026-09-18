using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loam.Revit.Connector.RevitBridge;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// General element enumeration — the identity primitive the other tools assume exists but
    /// don't provide: filter_elements_by_scope_box needs a scope box first, get_element_by_uniqueid
    /// needs an id you already have, and the typed getters (rooms/levels/views/sheets/links) each
    /// cover one narrow category. This is the plain "give me elements" tool: optionally scoped to a
    /// category, unscoped it walks the whole document lazily bounded by limit (no full-document scan
    /// — Take() short-circuits the collector), so a caller with no prior identity can still discover
    /// what's in the model and join it via unique_id/ifc_guid.
    /// </summary>
    public sealed class ListElementsTool : IPdraTool
    {
        public string Name        => "pdra_list_elements";
        public string Description =>
            "Enumerate model elements — the general identity primitive (filter_elements_by_scope_box needs a " +
            "scope box, get_element_by_uniqueid needs an id you already have; this needs neither). Pass " +
            "category (a BuiltInCategory enum name, e.g. OST_Walls, or the document's display name, e.g. Walls) " +
            "to scope to one category; omit it to walk the whole document (bounded by limit — no category " +
            "means no natural sort, results come in document order). Each row carries unique_id (primary join " +
            "key), id, category (display name), category_id (BuiltInCategory enum name, when the category is " +
            "a built-in one — feed this back as the category arg), name, type_id, type_name (the element's " +
            "own type, when it has a distinct one), mark (ALL_MODEL_MARK — the human-facing tag, e.g. \"D-104\", " +
            "on drawings and in emails), design_option (id/name/is_primary — omitted when the element lives in " +
            "the main model), from_link (always present — this tool only walks the host document, so it is " +
            "always false here; see get_element_by_uniqueid for elements resolved inside a link), room " +
            "(id/name/number/level_name — the room enclosing the element's location, geometrically resolved; " +
            "omitted when unresolvable), ifc_guid (when present), " +
            "model_instance_id (when resolvable — the document-instance guard: unique_id/ifc_guid are " +
            "unique only WITHIN one document, so elements from two different reads only prove the same " +
            "real element when model_instance_id also matches), " +
            "level (when resolvable), and classification (assembly/OmniClass codes, plus classification_params " +
            "when passed, when populated). Pass params[] to also read named parameters, typed (storage_type/" +
            "value/unit/display) under their own row[\"params\"][name] key — never merged into classification{} " +
            "— use pdra_get_element_parameters first to discover what a given element actually carries. " +
            "Supports limit, fields, view_id (scope a category query to one " +
            "view), and classification_params. Pass offset on EVERY call once paging through a large " +
            "result (0 for the first page) for a stable ElementId-ascending page boundary across calls " +
            "— omitting offset entirely uses the original fast, unordered document-order walk for a " +
            "one-shot enumeration; the two orderings do not compose, so don't mix an offset-less call " +
            "with an offset call for the same walk. next_offset (present when truncated and offset was " +
            "passed) is the offset to pass for the following page. The response also carries " +
            "classification_sources — see that arg's description for what it tells you.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["category"] = new JsonObject { ["type"] = "string", ["description"] = "Category to scope to — the BuiltInCategory enum name (e.g. OST_Walls, OST_Doors) or the document's display name (e.g. Walls), enum name tried first. Omit to enumerate the whole document." },
                ["view_id"]  = new JsonObject { ["type"] = "integer", ["description"] = "Limit a category query to elements visible in this view." },
                ["limit"]    = JsonHelpers.LimitSchemaProp(def: 200, max: 2000),
                ["offset"]   = new JsonObject
                {
                    ["type"]        = "integer",
                    ["description"] = "Skip this many elements before returning limit rows. Pass it (0 for " +
                                       "the first page) on EVERY call once paging, for a stable ElementId-" +
                                       "ascending ordering across calls; omitting it entirely uses the " +
                                       "original fast, unordered document-order walk — the two orderings " +
                                       "don't compose. Use the response's next_offset for the following page.",
                },
                ["fields"]   = JsonHelpers.FieldsSchemaProp(),
                ["params"]   = new JsonObject
                {
                    ["type"]        = "array",
                    ["items"]       = new JsonObject { ["type"] = "string" },
                    ["description"] = "Extra parameter names to read per element, typed (storage_type/value/unit/" +
                                       "display — see pdra_get_element_parameters) under their own row[\"params\"]" +
                                       "[name] key, never merged into classification{}. unit is the parameter's " +
                                       "Revit-internal unit (feet for length, radians for angle, …) when it has " +
                                       "one; display is the human AsValueString() formatting.",
                },
                ["classification_params"] = JsonHelpers.ClassificationParamsSchemaProp(),
            },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            // Every row this call returns lives in the ONE host document (no link traversal here), so
            // this is computed once and reused — see ModelFacts.ModelInstanceId's own comment for why
            // a caller needs this to safely join unique_id/ifc_guid across calls.
            var modelInstanceId = ModelFacts.From(doc).ModelInstanceId;
            var defaultPhase = ElementContextReader.DefaultPhase(doc, ctx.UiApp.ActiveUIDocument);

            var limit  = args.GetLimit(def: 200, max: 2000);
            var fields = args.GetFields();
            var paramNames = args.GetStringArray("params");
            var clsParams = args.GetStringArray("classification_params");
            var clsEnvelope = ElementContextReader.NewClassificationEnvelope(clsParams);

            // B4 — PAGING: offset is opt-in (see the tool's own inputSchema/description for why an
            // offset-less call keeps the original fast path instead of always sorting).
            var hasOffset = args.TryGetInt("offset", out var offsetRaw);
            var offset = hasOffset ? Math.Max(0, offsetRaw) : 0;

            View? scopeView = null;
            if (args.TryGetLong("view_id", out var vid)) scopeView = doc.GetElement(new ElementId(vid)) as View;

            IEnumerable<Element> query;
            if (args.TryGetString("category", out var catName))
            {
                if (!CategoryResolver.TryResolve(doc, catName, out var bic, out var catErr))
                    return ToolResult.Error(catErr!);
                query = (scopeView is not null
                        ? new FilteredElementCollector(doc, scopeView.Id)
                        : new FilteredElementCollector(doc))
                    .OfCategory(bic).WhereElementIsNotElementType().Cast<Element>();
            }
            else
            {
                // No category: the whole document. Stays cheap because FilteredElementCollector's
                // enumerator is lazy and Take() below short-circuits it — this never materialises a
                // full list of every element in a large model just to return `limit` of them.
                query = new FilteredElementCollector(doc).WhereElementIsNotElementType().Cast<Element>();
            }

            List<Element> page;
            bool truncated;
            if (hasOffset)
            {
                // Paging path: materialize + sort by ElementId ascending for a stable, reproducible
                // page boundary across calls — FilteredElementCollector's own enumeration order is
                // otherwise unspecified (the tool's description already tells callers not to mix this
                // with an offset-less call). Costs a full walk of `query` up front instead of the lazy
                // Take() below; accepted only when the caller actually asked for offset paging.
                var ordered = query.OrderBy(e => e.Id.Value).ToList();
                page = ordered.Skip(offset).Take(limit + 1).ToList();
                truncated = page.Count > limit;
                if (truncated) page.RemoveAt(page.Count - 1);
            }
            else
            {
                // Fetch one extra to detect truncation without a separate (expensive) full count.
                page = query.Take(limit + 1).ToList();
                truncated = page.Count > limit;
                if (truncated) page.RemoveAt(page.Count - 1);
            }

            var rows = new JsonArray();
            foreach (var el in page)
            {
                var row = new JsonObject
                {
                    ["unique_id"] = el.UniqueId,
                    ["id"]        = el.Id.Value,
                    ["category"]  = el.Category?.Name,
                    ["name"]      = el.Name,
                };

                if (CategoryResolver.CategoryId(el.Category) is { } catId) row["category_id"] = catId;

                var (typeId, typeName) = ElementContextReader.ResolveType(el);
                if (typeId is not null) row["type_id"] = typeId.Value;
                if (typeName is not null) row["type_name"] = typeName;

                if (ElementContextReader.ResolveMark(el) is { Length: > 0 } mark) row["mark"] = mark;

                // This tool only ever walks the host document (no link traversal, unlike
                // get_element_by_uniqueid) — always false here, not omitted, matching
                // filter_elements_by_scope_box's own convention of reporting from_link as a fact,
                // never guessing it away.
                row["from_link"] = false;

                var designOption = ElementContextReader.ResolveDesignOption(el);
                if (designOption is not null) row["design_option"] = designOption;

                var room = ElementContextReader.ResolveRoom(el, defaultPhase);
                if (room is not null) row["room"] = room;

                var ifcGuid = el.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
                if (!string.IsNullOrEmpty(ifcGuid)) row["ifc_guid"] = ifcGuid;
                if (!string.IsNullOrEmpty(modelInstanceId)) row["model_instance_id"] = modelInstanceId;

                var level = ElementContextReader.ResolveLevel(el);
                if (level is not null) row["level"] = level;

                var cls = ElementContextReader.ResolveClassification(el, clsParams);
                clsEnvelope.Record(cls);
                if (cls is not null) row["classification"] = cls;

                if (paramNames is not null)
                {
                    JsonObject? pobj = null;
                    foreach (var pn in paramNames)
                    {
                        var v = ElementContextReader.ReadParamTyped(el, pn);
                        if (v is null) continue;
                        pobj ??= new JsonObject();
                        pobj[pn] = v;
                    }
                    if (pobj is not null) row["params"] = pobj;
                }

                rows.Add(JsonHelpers.Project(row, fields));
            }

            var result = new JsonObject
            {
                ["count"]                 = rows.Count,
                ["truncated"]             = truncated,
                ["elements"]              = rows,
                ["classification_sources"] = clsEnvelope.Build(),
            };
            if (hasOffset)
            {
                result["offset"] = offset;
                if (truncated) result["next_offset"] = offset + rows.Count;
            }
            if (!string.IsNullOrEmpty(modelInstanceId)) result["model_instance_id"] = modelInstanceId;
            return ToolResult.Ok(JsonHelpers.Serialize(result));
        }
    }
}
