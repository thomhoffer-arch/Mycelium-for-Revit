using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Resolves one or more Revit UniqueIds to their elements, searching the host
    /// document and then every loaded Revit link. Unlike pdra_get_element_by_ifcguid
    /// (which matches the stored IFC_GUID parameter, empty on most native elements),
    /// UniqueId is intrinsic to every element, so this always resolves an element that
    /// exists — including elements that live inside linked models.
    /// </summary>
    public sealed class GetElementByUniqueIdTool : IPdraTool
    {
        public string Name        => "pdra_get_element_by_uniqueid";
        public string Description =>
            "Find Revit element(s) by UniqueId — the always-available identity key (pass unique_id or " +
            "unique_ids[]). Prefer over pdra_get_element_by_ifcguid: IFC_GUID is empty on most native " +
            "elements, whereas UniqueId is intrinsic to every element. Searches the host model AND every " +
            "loaded Revit link; a link hit carries from_link, link_instance_id, link_title and the link's " +
            "own projectKey. Each result also returns level and classification (assembly/omniclass) — the " +
            "way to read the storey/Assembly code of elements INSIDE linked models, which the host-only " +
            "param tools cannot reach. Plus spine keys (source, sourceLocalId, projectKey) and ifc_guid " +
            "when present; found=false for ids with no match. Accepts classification_params to also probe " +
            "an office's own classification parameter; the response carries classification_sources.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["unique_id"]  = new JsonObject { ["type"] = "string", ["description"] = "A single Revit UniqueId to resolve." },
                ["unique_ids"] = new JsonObject
                {
                    ["type"]  = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Multiple Revit UniqueIds to resolve in one call.",
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

            var wanted = new List<string>();
            if (args.TryGetString("unique_id", out var single) && single.Length > 0) wanted.Add(single);
            if (args.TryGetProperty("unique_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) { var v = e.GetString(); if (!string.IsNullOrEmpty(v)) wanted.Add(v!); }

            if (wanted.Count == 0)
                return ToolResult.Error("Provide 'unique_id' (string) or 'unique_ids' (array of strings).");

            // Loaded Revit links, resolved lazily and only once (host miss is the common path).
            List<RevitLinkInstance>? links = null;

            var results = new JsonArray();
            foreach (var uid in wanted)
            {
                // 1) Host document.
                var host = SafeGet(doc, uid);
                if (host is not null) { results.Add(BuildFound(uid, host, link: null, clsParams, clsEnvelope)); continue; }

                // 2) Each loaded link's document.
                links ??= new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();

                Element? linked = null; RevitLinkInstance? foundLink = null;
                foreach (var rli in links)
                {
                    var ld = rli.GetLinkDocument();      // null when the link is unloaded
                    if (ld is null) continue;
                    var e = SafeGet(ld, uid);
                    if (e is not null) { linked = e; foundLink = rli; break; }
                }

                results.Add(linked is not null
                    ? BuildFound(uid, linked, foundLink, clsParams, clsEnvelope)
                    : new JsonObject { ["unique_id"] = uid, ["found"] = false });
            }

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject
            {
                ["count"]                  = results.Count,
                ["elements"]               = results,
                ["classification_sources"] = clsEnvelope.Build(),
            }));
        }

        private static Element? SafeGet(Document d, string uid)
        {
            try { return d.GetElement(uid); } catch { return null; }
        }

        /// <summary>Build the row for a resolved element. Spine keys come from the
        /// element's OWN document (host or link), so a linked element gets the link's
        /// projectKey. <paramref name="link"/> is the host-side instance when found in a link.</summary>
        private static JsonObject BuildFound(
            string uid, Element el, RevitLinkInstance? link,
            List<string>? clsParams, ElementContextReader.ClassificationEnvelope clsEnvelope)
        {
            var d        = el.Document;
            var typeId   = el.GetTypeId();
            var typeElem = typeId != ElementId.InvalidElementId ? d.GetElement(typeId) : null;

            var row = new JsonObject
            {
                ["unique_id"] = uid,
                ["found"]     = true,
                ["id"]        = el.Id.Value,
                ["name"]      = el.Name,
                ["category"]  = el.Category?.Name,
                ["type_id"]   = typeId != ElementId.InvalidElementId ? (JsonNode?)typeId.Value : null,
                ["type_name"] = typeElem?.Name,
            };
            if (CategoryResolver.CategoryId(el.Category) is { } catId) row["category_id"] = catId;

            var ifc = el.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
            if (!string.IsNullOrEmpty(ifc)) row["ifc_guid"] = ifc;

            SpineKeys.Add(row, el, SpineKeys.ProjectKey(d));

            // Storey + classification from the element's OWN document — works for linked
            // elements too (host param tools can't reach inside links).
            var level = ElementContextReader.ResolveLevel(el);
            if (level is not null) row["level"] = level;

            var cls = ElementContextReader.ResolveClassification(el, clsParams);
            clsEnvelope.Record(cls);
            if (cls is not null) row["classification"] = cls;

            if (link is not null)
            {
                row["from_link"]         = true;
                row["link_instance_id"]  = link.Id.Value;   // the RevitLinkInstance in the host model
                row["link_title"]        = d.Title;
            }

            return row;
        }
    }
}
