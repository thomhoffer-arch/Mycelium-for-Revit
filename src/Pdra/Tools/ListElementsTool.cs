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
            "a built-in one — feed this back as the category arg), name, ifc_guid (when present), " +
            "model_instance_id (when resolvable — the document-instance guard: unique_id/ifc_guid are " +
            "unique only WITHIN one document, so elements from two different reads only prove the same " +
            "real element when model_instance_id also matches), " +
            "level (when resolvable), and classification (assembly/OmniClass codes, plus classification_params " +
            "when passed, when populated). Supports limit, fields, view_id (scope a category query to one " +
            "view), and classification_params. The response also carries classification_sources — see that " +
            "arg's description for what it tells you.";

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
                ["fields"]   = JsonHelpers.FieldsSchemaProp(),
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

            var limit  = args.GetLimit(def: 200, max: 2000);
            var fields = args.GetFields();
            var clsParams = args.GetStringArray("classification_params");
            var clsEnvelope = ElementContextReader.NewClassificationEnvelope(clsParams);

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

            // Fetch one extra to detect truncation without a separate (expensive) full count.
            var page = query.Take(limit + 1).ToList();
            var truncated = page.Count > limit;
            if (truncated) page.RemoveAt(page.Count - 1);

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

                var ifcGuid = el.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
                if (!string.IsNullOrEmpty(ifcGuid)) row["ifc_guid"] = ifcGuid;
                if (!string.IsNullOrEmpty(modelInstanceId)) row["model_instance_id"] = modelInstanceId;

                var level = ElementContextReader.ResolveLevel(el);
                if (level is not null) row["level"] = level;

                var cls = ElementContextReader.ResolveClassification(el, clsParams);
                clsEnvelope.Record(cls);
                if (cls is not null) row["classification"] = cls;

                rows.Add(JsonHelpers.Project(row, fields));
            }

            var result = new JsonObject
            {
                ["count"]                 = rows.Count,
                ["truncated"]             = truncated,
                ["elements"]              = rows,
                ["classification_sources"] = clsEnvelope.Build(),
            };
            if (!string.IsNullOrEmpty(modelInstanceId)) result["model_instance_id"] = modelInstanceId;
            return ToolResult.Ok(JsonHelpers.Serialize(result));
        }
    }
}
