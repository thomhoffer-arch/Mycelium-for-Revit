using Autodesk.Revit.DB;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Discovery-first parameter surface for ONE element: every parameter Revit exposes on it,
    /// named + typed + whether it has a value — the capability the Loam repo's docs/CONNECTORS.md
    /// already documented as existing (get_element_parameters) before this connector actually
    /// implemented it. Complements list_elements'/filter_elements_by_scope_box's params[] and the
    /// classification_params arg (both of which need the caller to already know a name) by
    /// answering "what parameters does THIS element even have" first. Reuses
    /// ElementContextReader.ReadParamTyped and the type-then-instance sampling pattern
    /// pdra_get_classification_sources already established, rather than a third reader.
    /// </summary>
    public sealed class GetElementParametersTool : IPdraTool
    {
        public string Name        => "pdra_get_element_parameters";
        public string Description =>
            "List every parameter on ONE element — the discovery-first surface for 'what CAN I ask for on " +
            "this element' before naming a parameter in list_elements'/filter_elements_by_scope_box's " +
            "params[] or classification_params elsewhere. Identify the element by unique_id (preferred) or " +
            "id (numeric ElementId). Each row carries name, storage_type (String/Integer/Double/ElementId/" +
            "None), has_value, value (typed: a real string/number, or the raw numeric ElementId — never a " +
            "formatted display string), unit (Revit-internal unit — feet for length, radians for angle, … " +
            "— present only for Double storage with a recognised unit), and display (the human " +
            "AsValueString() formatting, when non-empty). Pass type_params=true to also include the " +
            "element's TYPE's parameters, each row flagged is_type: true.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["unique_id"]   = new JsonObject { ["type"] = "string",  ["description"] = "The element's UniqueId (preferred — see pdra_get_element_by_uniqueid)." },
                ["id"]          = new JsonObject { ["type"] = "integer", ["description"] = "The element's numeric ElementId (alternative to unique_id)." },
                ["type_params"] = new JsonObject { ["type"] = "boolean", ["description"] = "Also include the element's TYPE's parameters, each flagged is_type: true. Default false (instance parameters only)." },
            },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            Element? el = null;
            if (args.TryGetString("unique_id", out var uid) && uid.Length > 0)
            {
                try { el = doc.GetElement(uid); } catch { el = null; }
            }
            else if (args.TryGetLong("id", out var idv))
            {
                el = doc.GetElement(new ElementId(idv));
            }

            if (el is null)
                return ToolResult.Error("Provide 'unique_id' or 'id' that resolves to an element in the active document.");

            var includeType = args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("type_params", out var tp) && tp.ValueKind == JsonValueKind.True;

            var rows = new JsonArray();
            foreach (Parameter p in el.Parameters)
                rows.Add(ParamRow(p, isType: false));

            if (includeType)
            {
                var typeId = el.GetTypeId();
                if (typeId != ElementId.InvalidElementId && doc.GetElement(typeId) is Element typeElem)
                    foreach (Parameter p in typeElem.Parameters)
                        rows.Add(ParamRow(p, isType: true));
            }

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject
            {
                ["unique_id"] = el.UniqueId,
                ["id"]        = el.Id.Value,
                ["count"]     = rows.Count,
                ["params"]    = rows,
            }));
        }

        private static JsonObject ParamRow(Parameter p, bool isType)
        {
            var row = ElementContextReader.ReadParamTyped(p);
            row["name"] = p.Definition?.Name;
            if (isType) row["is_type"] = true;
            return row;
        }
    }
}
