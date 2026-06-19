using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Returns the active document's project identity from
    /// <see cref="Autodesk.Revit.DB.Document.ProjectInformation"/> so an external
    /// tool (e.g. Loam) can attribute and join data to the right project without
    /// any manual seeding. <c>name</c> and <c>number</c> are the join keys;
    /// <c>client</c>/<c>address</c>/<c>building</c> are optional context.
    /// </summary>
    public sealed class GetProjectInfoTool : IPdraTool
    {
        public string Name        => "pdra_get_project_info";
        public string Description =>
            "Returns the active Revit document's project identity from Document.ProjectInformation: " +
            "name and number (required join keys), plus optional client, address and building. Use this " +
            "to attribute model data to a project so an external tool (e.g. Loam) can join email/model by " +
            "project name without manual seeding.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        public JsonNode InputSchema => JsonHelpers.EmptyObjectSchema();

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            var pi = doc.ProjectInformation;

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject
            {
                ["name"]     = pi?.Name         ?? "",
                ["number"]   = pi?.Number       ?? "",
                ["client"]   = pi?.ClientName   ?? "",
                ["address"]  = pi?.Address      ?? "",
                ["building"] = pi?.BuildingName ?? "",
            }));
        }
    }
}
