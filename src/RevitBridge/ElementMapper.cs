using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Loam.Revit.Connector.Profiles;

namespace Loam.Revit.Connector.RevitBridge
{
    /// Translates Revit Element -> Loam tool DTOs. All field names must match the
    /// Loam model-source contract exactly; rename here breaks ingestion.
    public static class ElementMapper
    {
        public static object ToScopeBoxRow(Element e, string categoryName, bool inBox)
        {
            var levelName = ResolveLevelName(e);
            var (optName, optPrimary) = ResolveDesignOption(e);
            return new
            {
                unique_id = e.UniqueId,
                id = e.Id.IntegerValue,
                ifc_guid = GetParam(e, "IfcGUID"),
                category = categoryName,
                in_box = inBox,
                level_name = levelName,
                design_option_name = optName,
                design_option_is_primary = optPrimary,
                from_link = e.Document.IsLinked
            };
        }

        public static object ToFullElement(Element e, Profile profile, bool found)
        {
            if (!found || e == null)
                return new { unique_id = (string)null, found = false };

            var classification = BuildClassification(e, profile);
            return new
            {
                unique_id = e.UniqueId,
                found = true,
                ifc_guid = GetParam(e, "IfcGUID"),
                name = e.Name,
                type_name = ResolveTypeName(e),
                level_name = ResolveLevelName(e),
                classification
            };
        }

        public static string ResolveTypeName(Element e)
        {
            if (e is ElementType et) return et.Name;
            var typeId = e.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var t = e.Document.GetElement(typeId);
                if (t != null) return t.Name;
            }
            return null;
        }

        public static string ResolveLevelName(Element e)
        {
            if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId)
            {
                var lvl = e.Document.GetElement(e.LevelId) as Level;
                if (lvl != null) return lvl.Name;
            }
            var p = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                 ?? e.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM)
                 ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            if (p?.AsElementId() is { } eid && eid != ElementId.InvalidElementId)
            {
                if (e.Document.GetElement(eid) is Level l) return l.Name;
            }
            return null;
        }

        private static (string name, bool? isPrimary) ResolveDesignOption(Element e)
        {
            var opt = e.DesignOption;
            if (opt == null) return (null, null);
            var primaryParam = opt.get_Parameter(BuiltInParameter.OPTION_DESIGN_OPTION_PRIMARY_PARAM);
            bool? primary = primaryParam != null ? primaryParam.AsInteger() == 1 : (bool?)null;
            return (opt.Name, primary);
        }

        public static object BuildClassification(Element e, Profile profile)
        {
            string code = null, desc = null;
            foreach (var p in profile.ClassificationCodeParams ?? new List<string>())
            {
                var v = GetParamOnInstanceOrType(e, p);
                if (!string.IsNullOrWhiteSpace(v)) { code = v.Trim(); break; }
            }
            foreach (var p in profile.ClassificationDescriptionParams ?? new List<string>())
            {
                var v = GetParamOnInstanceOrType(e, p);
                if (!string.IsNullOrWhiteSpace(v)) { desc = v.Trim(); break; }
            }

            // Profile-defined shape check; emit null when the value doesn't match
            // (Loam's nl profile requires NL-SfB shape).
            if (code != null && !string.IsNullOrEmpty(profile.ClassificationCodeRegex))
            {
                if (!Regex.IsMatch(code, profile.ClassificationCodeRegex)) code = null;
            }

            if (code == null && desc == null) return null;
            return new { assembly_code = code, assembly_description = desc };
        }

        public static string GetParam(Element e, string name)
        {
            var p = e?.LookupParameter(name);
            if (p == null) return null;
            return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
        }

        public static string GetParamOnInstanceOrType(Element e, string name)
        {
            var v = GetParam(e, name);
            if (!string.IsNullOrEmpty(v)) return v;
            var typeId = e.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var t = e.Document.GetElement(typeId);
                if (t != null) return GetParam(t, name);
            }
            return null;
        }

        public static double? GetParamDouble(Element e, string name)
        {
            var p = e?.LookupParameter(name);
            if (p == null) return null;
            if (p.StorageType == StorageType.Double)
            {
                // Revit stores lengths in internal feet — convert to mm.
                return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
            }
            if (p.StorageType == StorageType.Integer) return p.AsInteger();
            if (p.StorageType == StorageType.String && double.TryParse(p.AsString(), out var d)) return d;
            return null;
        }
    }
}
