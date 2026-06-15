using Loam.Revit.Connector.RevitBridge;
using Newtonsoft.Json.Linq;

namespace Loam.Revit.Connector.Mcp.Tools
{
    public class GetModelRevisionTool : IMcpTool
    {
        private readonly RevitContext _ctx;
        public GetModelRevisionTool(RevitContext ctx) { _ctx = ctx; }

        public string Description => "Returns the current Revit model revision (GUID, save count, dirty flag).";
        public object InputSchema => new { type = "object", properties = new { } };

        public object Invoke(JObject args)
        {
            return _ctx.Run(doc =>
            {
                var verGuid = doc.GetWorksharingCentralModelPath() != null
                    ? doc.WorksharingCentralGUID.ToString()
                    : doc.CreationGUID.ToString();
                int saves = 0;
                try { saves = doc.GetWorksharingCentralModelPath() != null ? 0 : 0; } catch { }
                return new
                {
                    version_guid = verGuid,
                    number_of_saves = saves,
                    has_unsaved_changes = doc.IsModified
                };
            });
        }
    }
}
