using System.Collections.Generic;
using Autodesk.Revit.DB;
using Loam.Revit.Connector.Profiles;
using Loam.Revit.Connector.RevitBridge;
using Newtonsoft.Json.Linq;

namespace Loam.Revit.Connector.Mcp.Tools
{
    public class GetElementByIfcGuidTool : IMcpTool
    {
        private readonly RevitContext _ctx;
        private readonly Profile _profile;
        public GetElementByIfcGuidTool(RevitContext ctx, Profile profile) { _ctx = ctx; _profile = profile; }

        public string Description => "Resolves elements by IfcGUID. Loam drops misses — only emit found=true rows.";
        public object InputSchema => new
        {
            type = "object",
            required = new[] { "ifc_guids" },
            properties = new { ifc_guids = new { type = "array", items = new { type = "string" } } }
        };

        public object Invoke(JObject args)
        {
            var ids = args["ifc_guids"]?.ToObject<string[]>() ?? System.Array.Empty<string>();
            var set = new HashSet<string>(ids);

            return _ctx.Run(doc =>
            {
                var elements = new List<object>();
                if (set.Count == 0) return new { elements };

                var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                foreach (var e in collector)
                {
                    var guid = ElementMapper.GetParam(e, "IfcGUID");
                    if (guid == null || !set.Contains(guid)) continue;
                    elements.Add(ElementMapper.ToFullElement(e, _profile, true));
                    set.Remove(guid);
                    if (set.Count == 0) break;
                }
                return new { elements };
            });
        }
    }
}
