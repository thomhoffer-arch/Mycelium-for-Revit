using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Enumerates ViewSheets, the views placed on each, and optionally the model
    /// elements visible in those views. sheet_number matches the raw Revit sheet
    /// number (e.g. "A101"), which is also the stem used for exported PDF filenames.
    /// When include_elements=true each element carries unique_id + ifc_guid (when
    /// present) + classification, the same fields as pdra_get_element_by_uniqueid.
    /// </summary>
    public sealed class GetSheetsTool : IPdraTool
    {
        public string Name => "pdra_get_sheets";
        public string Description =>
            "Enumerate drawing sheets (ViewSheets) with the views placed on each. Returns " +
            "sheet_number (matches PDF export filename stem, e.g. \"A101\"), sheet_name, " +
            "unique_id, and for each placed view: name, view_type, unique_id. Set " +
            "include_elements=true to also return the unique_id, ifc_guid (when present), " +
            "and classification of every model element visible in each view — can be slow on " +
            "large models, pair with element_limit. Filter to one sheet via sheet_number.";

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
                    ["description"] = "Return only the sheet whose SheetNumber equals this value (case-insensitive). Omit for all sheets.",
                },
                ["include_elements"] = new JsonObject
                {
                    ["type"]        = "boolean",
                    ["description"] = "When true, each view also lists model elements visible in it (unique_id, ifc_guid, classification). Pair with element_limit to cap output.",
                },
                ["element_limit"] = new JsonObject
                {
                    ["type"]        = "integer",
                    ["description"] = "Max elements returned per view when include_elements=true (default 100, max 1000).",
                },
                ["limit"]  = JsonHelpers.LimitSchemaProp(100, 500),
                ["fields"] = JsonHelpers.FieldsSchemaProp(),
            },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            args.TryGetString("sheet_number", out var filterNum);

            var includeElems = args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("include_elements", out var ieProp)
                && ieProp.ValueKind == JsonValueKind.True;

            var elemLimit = args.TryGetInt("element_limit", out var elRaw)
                ? JsonHelpers.Clamp(elRaw, 1, 1000) : 100;

            var limit  = args.GetLimit(100, 500);
            var fields = args.GetFields();

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

                                var cls = ElementContextReader.ResolveClassification(el);
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

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject
            {
                ["total"]     = all.Count,
                ["count"]     = rows.Count,
                ["truncated"] = rows.Count < all.Count,
                ["sheets"]    = rows,
            }));
        }
    }
}
