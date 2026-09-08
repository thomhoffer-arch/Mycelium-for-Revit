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
    /// Enumerates ViewSheets, the views placed on each, and optionally the model
    /// elements visible in those views. sheet_number matches the raw Revit sheet
    /// number (e.g. "A101"), which is also the stem used for exported PDF filenames.
    /// When include_elements=true (requires sheet_number — see the ROOT FIX note in
    /// Run) each element carries unique_id + ifc_guid (when present) + classification,
    /// the same fields as pdra_get_element_by_uniqueid.
    /// </summary>
    public sealed class GetSheetsTool : IPdraTool
    {
        public string Name => "pdra_get_sheets";
        public string Description =>
            "Enumerate drawing sheets (ViewSheets) with the views placed on each. Returns " +
            "sheet_number (matches PDF export filename stem, e.g. \"A101\"), sheet_name, " +
            "unique_id, and for each placed view: name, view_type, unique_id. The response also " +
            "carries model_instance_id (when resolvable) — the document-instance guard: a Revit " +
            "unique_id/ifc_guid is unique only WITHIN one document, so elements from two different " +
            "reads only prove the same real element when model_instance_id also matches. Set " +
            "include_elements=true to also return the unique_id, ifc_guid (when present), " +
            "model_instance_id, and " +
            "classification of every model element visible in one view — requires sheet_number " +
            "(fetching a view's visible-element set is what makes Revit regenerate that view's " +
            "graphics, shown in its status bar as \"Generating graphics for ...\"; scoping to one " +
            "sheet keeps that to the handful of views placed on it instead of every view in the " +
            "document). Accepts classification_params (see pdra_get_element_by_uniqueid); the " +
            "response carries classification_sources. For bulk/model-wide element enumeration — " +
            "e.g. resyncing after a large change — use pdra_list_elements instead, which walks the " +
            "document directly and never touches per-view graphics.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["sheet_number"] = new JsonObject
                {
                    ["type"]        = "string",
                    ["description"] = "Return only the sheet whose SheetNumber equals this value (case-insensitive). " +
                        "Omit for all sheets — required when include_elements=true.",
                },
                ["include_elements"] = new JsonObject
                {
                    ["type"]        = "boolean",
                    ["description"] = "When true, list model elements visible in the views on ONE sheet (unique_id, " +
                        "ifc_guid, classification) — requires sheet_number. Pair with element_limit to cap output. " +
                        "For model-wide enumeration use pdra_list_elements, not a sheet_number-less call here.",
                },
                ["element_limit"] = new JsonObject
                {
                    ["type"]        = "integer",
                    ["description"] = "Max elements returned per view when include_elements=true (default 100, max 1000).",
                },
                ["limit"]  = JsonHelpers.LimitSchemaProp(100, 500),
                ["fields"] = JsonHelpers.FieldsSchemaProp(),
                ["classification_params"] = JsonHelpers.ClassificationParamsSchemaProp(),
            },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            // Every sheet/view/element this call returns lives in the ONE host document, so this is
            // computed once and reused, not re-derived per row — see ModelFacts.ModelInstanceId's own
            // comment for why a caller needs this to safely join unique_id/ifc_guid across calls.
            var modelInstanceId = ModelFacts.From(doc).ModelInstanceId;

            args.TryGetString("sheet_number", out var filterNum);

            var includeElems = args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("include_elements", out var ieProp)
                && ieProp.ValueKind == JsonValueKind.True;

            // ROOT FIX (live report: "a whole list of views generating graphics" interrupting normal
            // Revit use) — include_elements walks each placed view with a view-scoped
            // FilteredElementCollector, which is what makes Revit regenerate that view's graphics
            // (the "Generating graphics for ..." status-bar message) if it isn't already cached.
            // Refusing this without sheet_number makes it structurally impossible for one call to do
            // that across every view in the document — no cap, no truncation, the disruptive access
            // pattern simply can't be expressed through this tool. Bulk/model-wide enumeration has a
            // correct, non-disruptive tool already: pdra_list_elements walks the document directly
            // (no view scoping, so no forced graphics regen) and is what a full resync should use.
            if (includeElems && string.IsNullOrEmpty(filterNum))
                return ToolResult.Error(
                    "include_elements=true requires sheet_number (scopes element enumeration to the " +
                    "views on one sheet). For model-wide element enumeration, use pdra_list_elements " +
                    "instead — it doesn't force per-view graphics regeneration.");

            var elemLimit = args.TryGetInt("element_limit", out var elRaw)
                ? JsonHelpers.Clamp(elRaw, 1, 1000) : 100;

            var limit  = args.GetLimit(100, 500);
            var fields = args.GetFields();
            var clsParams = args.GetStringArray("classification_params");
            var clsEnvelope = ElementContextReader.NewClassificationEnvelope(clsParams);

            IEnumerable<ViewSheet> query = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate);

            if (!string.IsNullOrEmpty(filterNum))
                query = query.Where(s =>
                    string.Equals(s.SheetNumber, filterNum, StringComparison.OrdinalIgnoreCase));

            var all  = query.OrderBy(s => s.SheetNumber).ToList();
            var page = all.Take(limit).ToList();

            var rows = new JsonArray();
            foreach (var sheet in page)
            {
                var row = new JsonObject
                {
                    ["unique_id"]    = sheet.UniqueId,
                    ["sheet_number"] = sheet.SheetNumber,
                    ["sheet_name"]   = sheet.Name,
                };

                // Current revision stamp (graceful: BIP may be absent in older API versions)
                try
                {
                    var revStr = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString();
                    if (!string.IsNullOrEmpty(revStr)) row["current_revision"] = revStr;
                }
                catch { /* BIP unavailable in this Revit version */ }

                var viewsArr = new JsonArray();
                foreach (var vpId in sheet.GetAllViewports())
                {
                    if (doc.GetElement(vpId) is not Viewport vp) continue;
                    if (doc.GetElement(vp.ViewId) is not View view) continue;

                    var vRow = new JsonObject
                    {
                        ["unique_id"] = view.UniqueId,
                        ["name"]      = view.Name,
                        ["view_type"] = view.ViewType.ToString(),
                    };

                    if (includeElems)
                    {
                        // sheet_number is required above whenever includeElems is set, so this loop
                        // only ever runs over the handful of views placed on ONE sheet — never every
                        // view in the document.
                        var elemArr = new JsonArray();
                        try
                        {
                            var collector = new FilteredElementCollector(doc, view.Id)
                                .WhereElementIsNotElementType()
                                .Cast<Element>()
                                .Where(e => e.Category != null);

                            var count = 0;
                            foreach (var el in collector)
                            {
                                if (count++ >= elemLimit) break;

                                var eRow = new JsonObject { ["unique_id"] = el.UniqueId };

                                var ifc = el.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
                                if (!string.IsNullOrEmpty(ifc)) eRow["ifc_guid"] = ifc;
                                if (!string.IsNullOrEmpty(modelInstanceId)) eRow["model_instance_id"] = modelInstanceId;

                                var cls = ElementContextReader.ResolveClassification(el, clsParams);
                                clsEnvelope.Record(cls);
                                if (cls != null) eRow["classification"] = cls;

                                elemArr.Add(eRow);
                            }
                        }
                        catch { /* view doesn't support element enumeration (e.g. schedules) */ }

                        vRow["elements"]           = elemArr;
                        vRow["elements_truncated"] = (int)elemArr.Count == elemLimit;
                    }

                    viewsArr.Add(vRow);
                }

                row["views"] = viewsArr;
                rows.Add(JsonHelpers.Project(row, fields));
            }

            var result = new JsonObject
            {
                ["total"]                  = all.Count,
                ["count"]                  = rows.Count,
                ["truncated"]               = rows.Count < all.Count,
                ["sheets"]                  = rows,
                ["classification_sources"]  = clsEnvelope.Build(),
            };
            if (!string.IsNullOrEmpty(modelInstanceId)) result["model_instance_id"] = modelInstanceId;
            return ToolResult.Ok(JsonHelpers.Serialize(result));
        }
    }
}
