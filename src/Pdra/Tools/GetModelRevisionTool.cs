using Autodesk.Revit.DB;
using System.Text.Json;
using System.Text.Json.Nodes;
// Reaches out of src/Pdra/ (a PDRA vendor drop) into src/RevitBridge/ on purpose: ModelFacts.From
// is the ONE place central-model / worksharing identity is derived, shared with the event push in
// LoamEventClient. Deriving it a second time here would let this tool's answer and the pushed event
// disagree — exactly the class of doc/implementation-drift bug ROADMAP.md's "Fixed" section is full of
// (unique_id/ifc_guid silently missing on a documented field, level/design_option written as null
// instead of omitted, ...). One derivation, two call sites.
using Loam.Revit.Connector.RevitBridge;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Returns a revision/freshness stamp for the active document so an external
    /// tool (e.g. ClashControl) can tell whether it is in sync with the live model.
    /// </summary>
    public sealed class GetModelRevisionTool : IPdraTool
    {
        public string Name        => "pdra_get_model_revision";
        public string Description =>
            "Returns a freshness stamp for the active Revit document: version_guid + number_of_saves " +
            "(from Document.GetDocumentVersion — changes on each save), has_unsaved_changes, title and " +
            "path. Use this to check whether another live tool (e.g. ClashControl) is talking about the " +
            "same model state before joining their data. Note: version_guid only advances on save, so " +
            "has_unsaved_changes flags in-session edits that the guid does not yet reflect. Also returns " +
            "worksharing (cloud / not_workshared / file_based_central / file_based_local / " +
            "file_based_unknown) plus central_model_path and, for a cloud (C4R) model, " +
            "cloud_project_guid/cloud_model_guid/cloud_region — the cross-user identity anchor, since " +
            "title/path alone identify only THIS user's local copy of a workshared model.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        public JsonNode InputSchema => JsonHelpers.EmptyObjectSchema();

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            var v = Document.GetDocumentVersion(doc);
            var facts = ModelFacts.From(doc);

            var result = new JsonObject
            {
                ["title"]               = doc.Title,
                ["path"]                = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
                ["version_guid"]        = v?.VersionGUID.ToString(),
                ["number_of_saves"]     = v?.NumberOfSaves,
                ["has_unsaved_changes"] = doc.IsModified,
                ["is_workshared"]       = doc.IsWorkshared,
                ["worksharing"]         = facts.Worksharing,
            };
            // Omit, never blank (see docs/CONTRACT.md) — unlike `path` above, these are new fields
            // with no legacy caller depending on a `null` placeholder, so there is no excuse to copy
            // that pre-existing wart onto them.
            if (facts.CentralModelPath is not null) result["central_model_path"] = facts.CentralModelPath;
            if (facts.CloudProjectGuid is not null) result["cloud_project_guid"] = facts.CloudProjectGuid;
            if (facts.CloudModelGuid   is not null) result["cloud_model_guid"]   = facts.CloudModelGuid;
            if (facts.CloudRegion      is not null) result["cloud_region"]       = facts.CloudRegion;

            return ToolResult.Ok(JsonHelpers.Serialize(result));
        }
    }
}
