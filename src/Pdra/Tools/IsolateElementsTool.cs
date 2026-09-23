using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// One of the model-log handoff plan's "acting on Revit" tools — temporarily isolates
    /// elements in the active view (Revit's own "Temporary Hide/Isolate" view mode). Reversible
    /// via <c>reset: true</c> or the user's own "Reset Temporary Hide/Isolate".
    /// </summary>
    public sealed class IsolateElementsTool : IPdraTool
    {
        public string Name => "pdra_isolate_elements";
        public string Description =>
            "Temporarily isolates up to 5,000 elements (by unique_id) in the active view, using " +
            "Revit's own Temporary Hide/Isolate view mode — reversible, and visibly marked in " +
            "Revit's UI (the view's border and a status-bar banner) so the user always sees it's " +
            "active. Pass reset: true (ids not required then) to clear it instead.";

        public Reversibility Reversibility => Reversibility.ModelOnly;
        public Verifiability Verifiability => Verifiability.Render;

        private const int MaxIds = 5000;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["ids"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = $"UniqueIds to isolate (max {MaxIds}). Ignored when reset: true.",
                },
                ["reset"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Clear temporary hide/isolate on the active view instead of applying it.",
                },
            },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var uidoc = ctx.UiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = uidoc?.ActiveView;
            if (doc is null || view is null) return ToolResult.Error("No active document/view.");

            var reset = args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("reset", out var r) && r.ValueKind == JsonValueKind.True;

            using var t = new Transaction(doc, reset ? "Reset temporary isolate" : "Isolate elements");
            t.Start();
            try
            {
                if (reset)
                {
                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                }
                else
                {
                    var ids = args.GetStringArray("ids");
                    if (ids is null || ids.Count == 0) { t.RollBack(); return ToolResult.Error("ids is required (or pass reset: true)."); }
                    if (ids.Count > MaxIds) { t.RollBack(); return ToolResult.Error($"At most {MaxIds} ids per call (got {ids.Count})."); }

                    var elementIds = new List<ElementId>();
                    foreach (var uid in ids)
                    {
                        var el = doc.GetElement(uid);
                        if (el is not null) elementIds.Add(el.Id);
                    }
                    if (elementIds.Count == 0) { t.RollBack(); return ToolResult.Error("None of the given ids resolved to an element."); }

                    view.IsolateElementsTemporary(elementIds);
                }
                t.Commit();
            }
            catch
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                throw;
            }

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject { ["reset"] = reset }));
        }
    }
}
