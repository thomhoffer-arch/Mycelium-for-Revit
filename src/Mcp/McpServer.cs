using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Loam.Revit.Connector.Mcp.Tools;
using Loam.Revit.Connector.Profiles;
using Loam.Revit.Connector.RevitBridge;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Loam.Revit.Connector.Mcp
{
    /// MCP server: JSON-RPC over Streamable HTTP. Implements `initialize`,
    /// `tools/list`, and `tools/call` for the 5 Loam contract tools.
    public class McpServer
    {
        private readonly string _prefix;
        private readonly string _token;
        private readonly RevitContext _ctx;
        private readonly Dictionary<string, IMcpTool> _tools;
        private HttpListener _listener;
        private bool _running;

        public McpServer(string prefix, string token, RevitContext ctx, Profile profile)
        {
            _prefix = prefix.EndsWith("/") ? prefix : prefix + "/";
            _token = string.IsNullOrEmpty(token) ? null : token;
            _ctx = ctx;
            _tools = new Dictionary<string, IMcpTool>
            {
                ["get_model_revision"]            = new GetModelRevisionTool(ctx),
                ["filter_elements_by_scope_box"]  = new FilterElementsByScopeBoxTool(ctx, profile),
                ["get_element_by_uniqueid"]       = new GetElementByUniqueIdTool(ctx, profile),
                ["get_element_by_ifcguid"]        = new GetElementByIfcGuidTool(ctx, profile),
                ["get_door_rooms"]                = new GetDoorRoomsTool(ctx, profile),
            };
        }

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(_prefix);
            _listener.Start();
            _running = true;
            Task.Run(AcceptLoop);
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        private async Task AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext http)
        {
            try
            {
                if (_token != null)
                {
                    var auth = http.Request.Headers["Authorization"];
                    if (auth != "Bearer " + _token)
                    {
                        http.Response.StatusCode = 401;
                        http.Response.Close();
                        return;
                    }
                }

                if (http.Request.HttpMethod != "POST")
                {
                    http.Response.StatusCode = 405;
                    http.Response.Close();
                    return;
                }

                using var reader = new StreamReader(http.Request.InputStream);
                var body = reader.ReadToEnd();
                var req = JsonConvert.DeserializeObject<JsonRpcRequest>(body);
                var resp = Dispatch(req);

                var json = JsonConvert.SerializeObject(resp);
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                http.Response.ContentType = "application/json";
                http.Response.ContentLength64 = bytes.Length;
                http.Response.OutputStream.Write(bytes, 0, bytes.Length);
                http.Response.Close();
            }
            catch (Exception ex)
            {
                try
                {
                    http.Response.StatusCode = 500;
                    using var sw = new StreamWriter(http.Response.OutputStream);
                    sw.Write(ex.Message);
                }
                catch { }
                http.Response.Close();
            }
        }

        private JsonRpcResponse Dispatch(JsonRpcRequest req)
        {
            try
            {
                switch (req.Method)
                {
                    case "initialize":
                        return Ok(req.Id, new
                        {
                            protocolVersion = "2024-11-05",
                            capabilities = new { tools = new { } },
                            serverInfo = new { name = "loam-revit-connector", version = "0.1.0" }
                        });

                    case "tools/list":
                        return Ok(req.Id, new { tools = ToolListing() });

                    case "tools/call":
                        var name = req.Params?["name"]?.ToString();
                        var args = req.Params?["arguments"] as JObject ?? new JObject();
                        if (name == null || !_tools.TryGetValue(name, out var tool))
                            return Err(req.Id, -32601, "Unknown tool: " + name);
                        var payload = tool.Invoke(args);
                        return Ok(req.Id, new ToolCallResult
                        {
                            Content = new[] { new ToolContent { Text = JsonConvert.SerializeObject(payload) } }
                        });

                    default:
                        return Err(req.Id, -32601, "Method not found: " + req.Method);
                }
            }
            catch (Exception ex)
            {
                return Err(req.Id, -32603, ex.Message);
            }
        }

        private object[] ToolListing()
        {
            var list = new List<object>();
            foreach (var kv in _tools)
                list.Add(new { name = kv.Key, description = kv.Value.Description, inputSchema = kv.Value.InputSchema });
            return list.ToArray();
        }

        private static JsonRpcResponse Ok(JToken id, object result) =>
            new JsonRpcResponse { Id = id, Result = result };
        private static JsonRpcResponse Err(JToken id, int code, string msg) =>
            new JsonRpcResponse { Id = id, Error = new JsonRpcError { Code = code, Message = msg } };
    }

    public interface IMcpTool
    {
        string Description { get; }
        object InputSchema { get; }
        object Invoke(JObject args);
    }
}
