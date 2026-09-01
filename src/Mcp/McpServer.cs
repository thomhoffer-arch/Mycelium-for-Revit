using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Loam.Revit.Connector.RevitBridge;
using PDRA.Services.Ai.Tools;
using PDRA.Services.Ai.Tools.Queries;

namespace Loam.Revit.Connector.Mcp
{
    /// MCP server: JSON-RPC over Streamable HTTP. Implements `initialize`,
    /// `tools/list`, and `tools/call`. Dispatches to PDRA's IPdraTool
    /// implementations — those are the canonical Revit ops; this class only
    /// owns the wire framing and the Revit UI-thread marshalling.
    public sealed class McpServer
    {
        private readonly string _prefix;
        private readonly string _token;
        private readonly RevitContext _ctx;
        private readonly Dictionary<string, IPdraTool> _tools;
        private HttpListener _listener;
        private bool _running;

        /// <summary>True when this instance successfully claimed the HTTP port.
        /// False when another Revit instance already owns it — the add-in loads
        /// silently and the first instance continues serving MCP requests.</summary>
        public bool IsListening { get; private set; }

        public McpServer(string listenUrl, string token, RevitContext ctx)
        {
            // Listen at host root so both /mcp and /mcp/ (and any subpath) hit us.
            // HttpListener prefix matching is strict on the trailing slash, and Loam's
            // default URL has none (LOAM_REVIT_URL=…:47100/mcp).
            var u = new Uri(listenUrl);
            _prefix = $"{u.Scheme}://{u.Host}:{u.Port}/";
            _token = string.IsNullOrEmpty(token) ? null : token;
            _ctx = ctx;

            // PDRA tool names ship prefixed (e.g. "pdra_get_model_revision").
            // Loam dials the unprefixed contract names — expose both, keyed by
            // contract name with the PDRA name as an alias.
            _tools = new Dictionary<string, IPdraTool>(StringComparer.Ordinal);
            foreach (var t in new IPdraTool[]
            {
                new GetModelRevisionTool(),
                new GetProjectInfoTool(),
                new ListElementsTool(),
                new FilterElementsByScopeBoxTool(),
                new GetElementByUniqueIdTool(),
                new GetElementByIfcGuidTool(),
                new GetDoorRoomsTool(),
                new GetClassificationSourcesTool(),
                new GetSheetsTool(),
                new GetLevelsTool(),
                new GetRoomsTool(),
                new GetViewsTool(),
                new GetLinksTool(),
            })
            {
                _tools[t.Name] = t;                       // pdra_*
                _tools[Strip(t.Name)] = t;                // contract name
            }
        }

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(_prefix);
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 32 || ex.ErrorCode == 183)
            {
                // Another Revit instance already owns the port — load silently.
                _listener = null;
                return;
            }
            IsListening = true;
            _running = true;
            Task.Run(AcceptLoop);
        }

        public void Stop()
        {
            _running = false;
            if (_listener is null) return;
            try { _listener.Stop(); } catch { /* listener already disposed */ }
        }

        private async Task AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext http;
                try { http = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => Handle(http));
            }
        }

        private void Handle(HttpListenerContext http)
        {
            try
            {
                if (_token is not null)
                {
                    var auth = http.Request.Headers["Authorization"];
                    if (auth != "Bearer " + _token)
                    {
                        http.Response.StatusCode = 401;
                        http.Response.Close();
                        return;
                    }
                }
                if (http.Request.HttpMethod == "GET")
                {
                    // Health check — connector is up and listening.
                    http.Response.StatusCode = 200;
                    http.Response.Close();
                    return;
                }
                if (http.Request.HttpMethod != "POST")
                {
                    http.Response.StatusCode = 405;
                    http.Response.Close();
                    return;
                }

                using var reader = new StreamReader(http.Request.InputStream);
                var body = reader.ReadToEnd();
                var respJson = Dispatch(body);

                var bytes = Encoding.UTF8.GetBytes(respJson);
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
                catch { /* response already disposed */ }
                http.Response.Close();
            }
        }

        private string Dispatch(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var id   = root.TryGetProperty("id", out var idEl) ? JsonNode.Parse(idEl.GetRawText()) : null;
            var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;

            try
            {
                return method switch
                {
                    "initialize" => Ok(id, new JsonObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"]    = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"]      = new JsonObject { ["name"] = "mycelium-revit-connector", ["version"] = "0.2.0" },
                    }),
                    "tools/list" => Ok(id, new JsonObject { ["tools"] = BuildToolListing() }),
                    "tools/call" => CallTool(id, root),
                    _            => Err(id, -32601, "Method not found: " + method),
                };
            }
            catch (Exception ex)
            {
                return Err(id, -32603, ex.Message);
            }
        }

        private string CallTool(JsonNode id, JsonElement root)
        {
            if (!root.TryGetProperty("params", out var p))
                return Err(id, -32602, "Missing params.");
            var name = p.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            if (name is null || !_tools.TryGetValue(name, out var tool))
                return Err(id, -32601, "Unknown tool: " + name);

            var args = p.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.Object
                ? JsonDocument.Parse(aEl.GetRawText())
                : JsonDocument.Parse("{}");

            ToolResult result;
            try
            {
                result = _ctx.Run(uiApp => tool.Run(new ToolContext(uiApp), args.RootElement));
            }
            finally
            {
                args.Dispose();
            }

            // PDRA's ToolResult.Text is already a JSON string — pass through as content[0].text.
            var content = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = result.Text },
            };
            var payload = new JsonObject { ["content"] = content };
            if (result.IsError) payload["isError"] = true;
            return Ok(id, payload);
        }

        private JsonArray BuildToolListing()
        {
            // Advertise the contract names (unprefixed) so Loam's client uses those.
            // Each is the same underlying IPdraTool — the dispatch table accepts both.
            var seen = new HashSet<IPdraTool>();
            var list = new JsonArray();
            foreach (var tool in _tools.Values)
            {
                if (!seen.Add(tool)) continue;
                list.Add(new JsonObject
                {
                    ["name"]        = Strip(tool.Name),
                    ["description"] = tool.Description,
                    ["inputSchema"] = JsonNode.Parse(tool.InputSchema.ToJsonString()),
                });
            }
            return list;
        }

        private static string Strip(string name) =>
            name.StartsWith("pdra_", StringComparison.Ordinal) ? name.Substring(5) : name;

        private static string Ok(JsonNode id, JsonNode result)
        {
            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"]      = id?.DeepClone(),
                ["result"]  = result,
            };
            return resp.ToJsonString();
        }

        private static string Err(JsonNode id, int code, string msg)
        {
            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"]      = id?.DeepClone(),
                ["error"]   = new JsonObject { ["code"] = code, ["message"] = msg },
            };
            return resp.ToJsonString();
        }
    }
}
