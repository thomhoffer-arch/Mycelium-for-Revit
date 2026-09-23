using Autodesk.Revit.DB;
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// One of the model-log handoff plan's "acting on Revit" tools — opens a sheet by its
    /// SheetNumber (the identity a person already knows, e.g. "A101" — matches the log's own
    /// <c>sheet</c> record and the PDF export filename stem).
    /// </summary>
    public sealed class OpenSheetTool : IPdraTool
    {
        public string Name => "pdra_open_sheet";
        public string Description =>
            "Opens the drawing sheet whose SheetNumber matches (case-insensitive), e.g. \"A101\".";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Render;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["number"] = new JsonObject { ["type"] = "string", ["description"] = "SheetNumber, e.g. \"A101\"." },
            },
            ["required"] = new JsonArray { "number" },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var uidoc = ctx.UiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc is null || uidoc is null) return ToolResult.Error("No active document.");

            if (!args.TryGetString("number", out var number)) return ToolResult.Error("number is required.");

            var sheet = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, number, StringComparison.OrdinalIgnoreCase));

            if (sheet is null) return ToolResult.Error($"No sheet with number '{number}'.");

            uidoc.RequestViewChange(sheet);

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject
            {
                ["opened"] = sheet.UniqueId,
                ["number"] = sheet.SheetNumber,
                ["name"] = sheet.Name,
            }));
        }
    }
}
