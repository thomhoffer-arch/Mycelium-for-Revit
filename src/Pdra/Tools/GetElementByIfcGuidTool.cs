using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Resolves one or more IFC GlobalIds to the matching Revit elements — the
    /// cross-tool join (e.g. correlating a ClashControl clash element with the
    /// Revit element by IfcGUID). Matches the element's stored IFC_GUID parameter.
    /// </summary>
    public sealed class GetElementByIfcGuidTool : IPdraTool
    {
        public string Name        => "pdra_get_element_by_ifcguid";
        public string Description =>
            "Find Revit element(s) by IFC GlobalId (IfcGUID) — the cross-tool join key. Pass a single " +
            "ifc_guid or an array ifc_guids[]. Matches each element's stored IFC_GUID parameter " +
            "(populated after an IFC export/round-trip). Returns [{ifc_guid, id, unique_id, name, " +
            "category, type_id, type_name, level, classification, found, source, sourceLocalId, " +
            "projectKey}] — the same element shape as pdra_get_element_by_uniqueid; found=false for " +
            "GUIDs with no match. Use to turn a ClashControl clash's globalIdA/globalIdB into the " +
            "corresponding Revit element. Accepts classification_params (see " +
            "pdra_get_element_by_uniqueid); the response carries classification_sources.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["ifc_guid"]  = new JsonObject { ["type"] = "string", ["description"] = "A single IFC GlobalId to resolve." },
                ["ifc_guids"] = new JsonObject
                {
                    ["type"]  = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Multiple IFC GlobalIds to resolve in one call.",
                },
                ["classification_params"] = JsonHelpers.ClassificationParamsSchemaProp(),
            },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            var clsParams = args.GetStringArray("classification_params");
            var clsEnvelope = ElementContextReader.NewClassificationEnvelope(clsParams);

            // Collect requested GUIDs (single and/or array).
            var wanted = new List<string>();
            if (args.TryGetString("ifc_guid", out var single) && single.Length > 0) wanted.Add(single);
            if (args.TryGetProperty("ifc_guids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) { var v = e.GetString(); if (!string.IsNullOrEmpty(v)) wanted.Add(v!); }

            if (wanted.Count == 0)
                return ToolResult.Error("Provide 'ifc_guid' (string) or 'ifc_guids' (array of strings).");

            // Index the model's stored IFC_GUID → element once, then resolve each request.
            var byGuid = new Dictionary<string, Element>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var p = el.get_Parameter(BuiltInParameter.IFC_GUID);
                var g = p?.AsString();
                if (!string.IsNullOrEmpty(g) && !byGuid.ContainsKey(g!)) byGuid[g!] = el;
            }

            var projectKey = SpineKeys.ProjectKey(doc);

            var results = new JsonArray();
            foreach (var guid in wanted)
            {
                if (byGuid.TryGetValue(guid, out var el))
                {
                    var typeId   = el.GetTypeId();
                    var typeElem = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
                    var row = new JsonObject
                    {
                        ["ifc_guid"]  = guid,
                        ["found"]     = true,
                        ["id"]        = el.Id.Value,
                        ["unique_id"] = el.UniqueId,
                        ["name"]      = el.Name,
                        ["category"]  = el.Category?.Name,
                        ["type_id"]   = typeId != ElementId.InvalidElementId ? (JsonNode?)typeId.Value : null,
                        ["type_name"] = typeElem?.Name,
                    };
                    SpineKeys.Add(row, el, projectKey);

                    var level = ElementContextReader.ResolveLevel(el);
                    if (level is not null) row["level"] = level;

                    var cls = ElementContextReader.ResolveClassification(el, clsParams);
                    clsEnvelope.Record(cls);
                    if (cls is not null) row["classification"] = cls;

                    results.Add(row);
                }
                else
                {
                    results.Add(new JsonObject { ["ifc_guid"] = guid, ["found"] = false });
                }
            }

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject
            {
                ["count"]                  = results.Count,
                ["elements"]               = results,
                ["classification_sources"] = clsEnvelope.Build(),
            }));
        }
    }
}
