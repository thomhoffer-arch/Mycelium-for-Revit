using Autodesk.Revit.DB;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Connective-spine v0 MUST keys (see Loam docs/CONNECTIVE_SPINE.md §1).
    /// Additive provenance keys every element-returning tool stamps onto each
    /// element object so downstream records can populate the provenance ledger:
    ///   • source         — constant "pdra"
    ///   • sourceLocalId   — the element's UniqueId (stable within the session)
    ///   • projectKey      — a stable id for the document, unique per project so
    ///                       records from two open models never collide
    /// (ifcGuid is already carried by the existing ifc_guid field/alias.)
    ///
    /// Rules honoured here: emit a key only with a real value (omit, never blank);
    /// purely additive — existing fields keep their meaning.
    /// </summary>
    internal static class SpineKeys
    {
        public const string Source = "pdra";

        /// <summary>Stamp the spine keys onto one element's output object.
        /// <paramref name="projectKey"/> is computed once per call via
        /// <see cref="ProjectKey"/> and passed in to avoid re-hashing per element.</summary>
        public static void Add(JsonObject row, Element el, string projectKey)
        {
            row["source"] = Source;

            var uid = el.UniqueId;
            if (!string.IsNullOrEmpty(uid))
            {
                // Add unique_id if a tool didn't already emit it; sourceLocalId is the spine name.
                if (row["unique_id"] is null) row["unique_id"] = uid;
                row["sourceLocalId"] = uid;
            }

            if (!string.IsNullOrEmpty(projectKey)) row["projectKey"] = projectKey;
        }

        /// <summary>A stable id for the document, unique per project and unchanged
        /// across saves, renames and moves. Matches ClashControl's join: the project's
        /// canonical id is the ProjectInformation element's UniqueId, prefixed
        /// <c>revit:</c> as a bootstrap. (A future explicitly-assigned project id, if
        /// one is stored, would take precedence.) Falls back to a path/title hash only
        /// when ProjectInformation is unavailable; returns "" when nothing is (callers
        /// then omit the key).</summary>
        public static string ProjectKey(Document doc)
        {
            try
            {
                var uid = doc.ProjectInformation?.UniqueId;
                if (!string.IsNullOrEmpty(uid)) return "revit:" + uid;
            }
            catch { /* fall through to path / title */ }

            if (!string.IsNullOrEmpty(doc.PathName)) return Hash(doc.PathName);

            return doc.Title ?? "";
        }

        private static string Hash(string s)
        {
            // SHA256.HashData / Convert.ToHexString are .NET 5+, absent on net48
            // (Revit 2024); use the instance API and manual hex instead.
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder(16);
            for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
