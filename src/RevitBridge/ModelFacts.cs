using System;
using Autodesk.Revit.DB;

namespace Loam.Revit.Connector.RevitBridge
{
    /// <summary>
    /// The one place central-model identity is derived from a Document — both the pushed
    /// event (<see cref="LoamEventClient"/>) and the get_model_revision MCP tool
    /// (<c>GetModelRevisionTool</c>) call <see cref="From"/> instead of deriving these
    /// fields twice, so the two can never drift apart (see ROADMAP.md's "Fixed" section
    /// for the class of doc/implementation-drift bug that pattern exists to prevent).
    ///
    /// WHY THIS EXISTS: doc.Title / doc.PathName identify a file-based WORKSHARED model's
    /// LOCAL COPY, which is different per user — user A reports "SFW_PDR_BWK_R24_thom.hoffer"
    /// at their own Documents folder, user B reports a different title and path — even
    /// though both are editing the SAME shared model. Loam cannot tell those two users'
    /// events concern one model from title/path alone. CentralModelPath (file-based) and
    /// CloudProjectGuid+CloudModelGuid (C4R) are the one fact that IS identical across every
    /// user of a model — the cross-user identity anchor.
    /// </summary>
    public sealed class ModelFacts
    {
        public string? Model { get; }
        public string? Project { get; }
        public string? Revision { get; }

        /// <summary>Never null — one of: "cloud", "not_workshared", "file_based_central",
        /// "file_based_local", "file_based_unknown".</summary>
        public string Worksharing { get; }

        public string? CentralModelPath { get; }
        public string? CloudProjectGuid { get; }
        public string? CloudModelGuid { get; }
        public string? CloudRegion { get; }

        public ModelFacts(
            string? model,
            string? project,
            string? revision,
            string worksharing,
            string? centralModelPath,
            string? cloudProjectGuid,
            string? cloudModelGuid,
            string? cloudRegion)
        {
            Model = model;
            Project = project;
            Revision = revision;
            Worksharing = string.IsNullOrEmpty(worksharing) ? "file_based_unknown" : worksharing;
            CentralModelPath = centralModelPath;
            CloudProjectGuid = cloudProjectGuid;
            CloudModelGuid = cloudModelGuid;
            CloudRegion = cloudRegion;
        }

        /// <summary>
        /// Derives every fact from a live Document. Each Revit call is individually
        /// wrapped — this runs from inside Revit document-event handlers (see App.cs),
        /// where an unhandled exception must never escape.
        /// </summary>
        public static ModelFacts From(Document doc)
        {
            string? model = null, project = null, revision = null;
            try { model = doc.Title; } catch { }

            try
            {
                var pi = doc.ProjectInformation;
                var number = pi?.Number ?? "";
                var name   = pi?.Name   ?? "";
                var p = $"{number} {name}".Trim();
                project = p.Length == 0 ? null : p;
            }
            catch { }

            try
            {
                var v = Document.GetDocumentVersion(doc);
                revision = v?.VersionGUID.ToString();
            }
            catch { }

            var worksharing = "file_based_unknown";
            string? centralModelPath = null;
            string? cloudProjectGuid = null;
            string? cloudModelGuid = null;
            string? cloudRegion = null;

            var isCloud = false;
            try { isCloud = doc.IsModelInCloud; } catch { }

            if (isCloud)
            {
                worksharing = "cloud";

                try
                {
                    var cloudPath = doc.GetCloudModelPath();
                    if (cloudPath is not null)
                    {
                        try { cloudProjectGuid = cloudPath.GetProjectGUID().ToString(); } catch { }
                        try { cloudModelGuid = cloudPath.GetModelGUID().ToString(); } catch { }
                        try { cloudRegion = cloudPath.Region; } catch { }
                    }
                }
                catch { /* cloud path unavailable — cloud fields stay null, worksharing is still "cloud" */ }

                // Best-effort: a cloud model also carries a resolvable central path in most
                // cases (BIM 360 / ACC), but this must never demote a known-cloud model to
                // one of the file_based_* states — leave CentralModelPath null on failure.
                try
                {
                    var wsCentral = doc.GetWorksharingCentralModelPath();
                    if (wsCentral is not null)
                    {
                        var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(wsCentral);
                        if (!string.IsNullOrEmpty(visible)) centralModelPath = visible;
                    }
                }
                catch { }
            }
            else
            {
                var isWorkshared = false;
                try { isWorkshared = doc.IsWorkshared; } catch { }

                if (!isWorkshared)
                {
                    worksharing = "not_workshared";
                }
                else
                {
                    // Not resolvable (throws / null / empty — e.g. a detached model) means we
                    // cannot tell central from local copy; do NOT guess — file_based_unknown,
                    // CentralModelPath stays null.
                    string? resolved = null;
                    try
                    {
                        var wsCentral = doc.GetWorksharingCentralModelPath();
                        if (wsCentral is not null)
                        {
                            var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(wsCentral);
                            if (!string.IsNullOrEmpty(visible)) resolved = visible;
                        }
                    }
                    catch { }

                    if (resolved is null)
                    {
                        worksharing = "file_based_unknown";
                    }
                    else
                    {
                        centralModelPath = resolved;

                        string? pathName = null;
                        try { pathName = doc.PathName; } catch { }

                        worksharing = !string.IsNullOrEmpty(pathName) &&
                                      string.Equals(resolved, pathName, StringComparison.OrdinalIgnoreCase)
                            ? "file_based_central"
                            : "file_based_local";
                    }
                }
            }

            return new ModelFacts(model, project, revision, worksharing, centralModelPath,
                cloudProjectGuid, cloudModelGuid, cloudRegion);
        }
    }
}
