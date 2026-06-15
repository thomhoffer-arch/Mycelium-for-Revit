using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Loam.Revit.Connector.Profiles;
using Loam.Revit.Connector.RevitBridge;
using Newtonsoft.Json.Linq;

namespace Loam.Revit.Connector.Mcp.Tools
{
    public class FilterElementsByScopeBoxTool : IMcpTool
    {
        private readonly RevitContext _ctx;
        private readonly Profile _profile;
        public FilterElementsByScopeBoxTool(RevitContext ctx, Profile profile) { _ctx = ctx; _profile = profile; }

        public string Description =>
            "Returns elements of a category whose bounding box intersects the given scope box. Set inside_only=true to require fully inside.";
        public object InputSchema => new
        {
            type = "object",
            required = new[] { "scope_box_id", "category" },
            properties = new
            {
                scope_box_id = new { type = "integer" },
                category = new { type = "string", description = "OST_* enum name" },
                inside_only = new { type = "boolean", @default = true }
            }
        };

        public object Invoke(JObject args)
        {
            int scopeBoxId = (int)args.Value<long>("scope_box_id");
            string categoryName = args.Value<string>("category");
            bool insideOnly = args["inside_only"]?.Value<bool>() ?? true;

            return _ctx.Run(doc =>
            {
                var scopeBox = doc.GetElement(new ElementId(scopeBoxId));
                if (scopeBox == null) return new { count_in = 0, elements = System.Array.Empty<object>() };
                var bbox = scopeBox.get_BoundingBox(null);

                if (!System.Enum.TryParse<BuiltInCategory>(categoryName, out var bic))
                    return new { count_in = 0, elements = System.Array.Empty<object>() };

                var coll = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType();

                var rows = new List<object>();
                int countIn = 0;
                foreach (var e in coll)
                {
                    var eb = e.get_BoundingBox(null);
                    if (eb == null) continue;
                    bool inside = BoxContains(bbox, eb, fully: insideOnly);
                    if (insideOnly && !inside) continue;
                    if (inside) countIn++;
                    rows.Add(ElementMapper.ToScopeBoxRow(e, categoryName, inside));
                }
                return new { count_in = countIn, elements = rows };
            });
        }

        private static bool BoxContains(BoundingBoxXYZ outer, BoundingBoxXYZ inner, bool fully)
        {
            if (outer == null || inner == null) return false;
            if (fully)
            {
                return inner.Min.X >= outer.Min.X && inner.Min.Y >= outer.Min.Y && inner.Min.Z >= outer.Min.Z
                    && inner.Max.X <= outer.Max.X && inner.Max.Y <= outer.Max.Y && inner.Max.Z <= outer.Max.Z;
            }
            return !(inner.Max.X < outer.Min.X || inner.Min.X > outer.Max.X
                  || inner.Max.Y < outer.Min.Y || inner.Min.Y > outer.Max.Y
                  || inner.Max.Z < outer.Min.Z || inner.Min.Z > outer.Max.Z);
        }
    }
}
