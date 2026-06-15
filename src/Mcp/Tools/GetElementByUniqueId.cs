using System.Collections.Generic;
using Loam.Revit.Connector.Profiles;
using Loam.Revit.Connector.RevitBridge;
using Newtonsoft.Json.Linq;

namespace Loam.Revit.Connector.Mcp.Tools
{
    public class GetElementByUniqueIdTool : IMcpTool
    {
        private readonly RevitContext _ctx;
        private readonly Profile _profile;
        public GetElementByUniqueIdTool(RevitContext ctx, Profile profile) { _ctx = ctx; _profile = profile; }

        public string Description => "Resolves elements by Revit UniqueId; returns found=false for misses.";
        public object InputSchema => new
        {
            type = "object",
            required = new[] { "unique_ids" },
            properties = new { unique_ids = new { type = "array", items = new { type = "string" } } }
        };

        public object Invoke(JObject args)
        {
            var ids = args["unique_ids"]?.ToObject<string[]>() ?? System.Array.Empty<string>();
            return _ctx.Run(doc =>
            {
                var elements = new List<object>();
                foreach (var uid in ids)
                {
                    var e = doc.GetElement(uid);
                    if (e == null) elements.Add(new { unique_id = uid, found = false });
                    else elements.Add(ElementMapper.ToFullElement(e, _profile, true));
                }
                return new { elements };
            });
        }
    }
}
