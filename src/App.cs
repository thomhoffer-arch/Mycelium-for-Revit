using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Loam.Revit.Connector.Mcp;
using Loam.Revit.Connector.Profiles;
using Loam.Revit.Connector.RevitBridge;

namespace Loam.Revit.Connector
{
    public class App : IExternalApplication
    {
        private McpServer _server;
        private RevitContext _context;

        public Result OnStartup(UIControlledApplication application)
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            var profile = ProfileLoader.Load(Path.Combine(asmDir, "Profiles", "nl.json"));

            _context = new RevitContext(application);
            var listen = Environment.GetEnvironmentVariable("LOAM_REVIT_LISTEN") ?? "http://127.0.0.1:47100/mcp/";
            var token = Environment.GetEnvironmentVariable("LOAM_REVIT_TOKEN");

            _server = new McpServer(listen, token, _context, profile);
            _server.Start();
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _server?.Stop();
            return Result.Succeeded;
        }
    }
}
