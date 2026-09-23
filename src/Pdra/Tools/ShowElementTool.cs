using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// One of the model-log handoff plan's "acting on Revit" tools — selects an element and
    /// zooms to it in the active view, "while the user watches" (docs/MODEL_LOG.md). Changes
    /// only the UI's current selection/view framing, never the model.
    /// </summary>
    public sealed class ShowElementTool : IPdraTool
    {
        public string Name => "pdra_show_element";
        public string Description =>
            "Selects the given element (by unique_id) and zooms the active view to it. " +
            "UI-only — never touches the model.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Render;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "UniqueId of the element to show." },
            },
            ["required"] = new JsonArray { "id" },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var uidoc = ctx.UiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc is null || uidoc is null) return ToolResult.Error("No active document.");

            if (!args.TryGetString("id", out var uid)) return ToolResult.Error("id is required.");
            var el = doc.GetElement(uid);
            if (el is null) return ToolResult.Error($"Unknown element unique_id '{uid}'.");

            var ids = new List<ElementId> { el.Id };
            uidoc.Selection.SetElementIds(ids);
            uidoc.ShowElements(ids);

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject { ["shown"] = uid }));
        }
    }
}
