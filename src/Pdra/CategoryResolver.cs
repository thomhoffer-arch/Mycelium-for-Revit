using Autodesk.Revit.DB;
using System;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Resolves a caller-supplied `category` argument to a Revit <see cref="BuiltInCategory"/>,
    /// and the reverse — reads the BuiltInCategory enum name back off an element's category for
    /// response rows. Every element-emitting tool's `category` output was the human display name
    /// (e.g. "Walls"), while every tool that accepts a `category` argument parses it as the enum
    /// name (e.g. "OST_Walls") via <c>Enum.TryParse</c> — so a caller that read `category` off one
    /// row and fed it back as an argument to another tool always got "Unknown BuiltInCategory"
    /// back. This closes that round trip in both directions: rows also carry `category_id` (the
    /// enum name), and `category` inputs accept either form, enum name tried first.
    /// </summary>
    internal static class CategoryResolver
    {
        /// <summary>Resolves <paramref name="input"/> to a BuiltInCategory: the enum name (e.g.
        /// "OST_Walls") first, then a case-insensitive match against the document's own category
        /// display names (e.g. "Walls") as a fallback — so a value round-tripped from a `category`
        /// field on another tool's row still resolves. Returns false with <paramref name="error"/>
        /// set when neither matches.</summary>
        public static bool TryResolve(Document doc, string input, out BuiltInCategory category, out string? error)
        {
            if (Enum.TryParse(input, ignoreCase: false, out category))
            {
                error = null;
                return true;
            }

            foreach (Category cat in doc.Settings.Categories)
            {
                if (!string.Equals(cat.Name, input, StringComparison.OrdinalIgnoreCase)) continue;
                var id = cat.Id.Value;
                if (id < int.MinValue || id > int.MaxValue) continue; // not a BuiltInCategory-range id
                category = (BuiltInCategory)id;
                error = null;
                return true;
            }

            category = default;
            error = $"Unknown category '{input}' (tried the BuiltInCategory enum name, e.g. OST_Walls, " +
                     "and the document's category display name, e.g. Walls).";
            return false;
        }

        /// <summary>The BuiltInCategory enum name for an element's category, for the `category_id`
        /// response field — or null when the category is a custom/family category with no
        /// corresponding BuiltInCategory (its Id is outside the built-in id range), so nothing
        /// meaningful can be reported (omit, don't blank).</summary>
        public static string? CategoryId(Category? cat)
        {
            if (cat is null) return null;
            var id = cat.Id.Value;
            if (id < int.MinValue || id > int.MaxValue) return null;
            var bic = (BuiltInCategory)id;
            return Enum.IsDefined(typeof(BuiltInCategory), bic) ? bic.ToString() : null;
        }
    }
}
