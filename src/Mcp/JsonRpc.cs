using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Loam.Revit.Connector.Mcp
{
    public class JsonRpcRequest
    {
        [JsonProperty("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonProperty("id")] public JToken Id { get; set; }
        [JsonProperty("method")] public string Method { get; set; }
        [JsonProperty("params")] public JObject Params { get; set; }
    }

    public class JsonRpcResponse
    {
        [JsonProperty("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonProperty("id")] public JToken Id { get; set; }
        [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
        public object Result { get; set; }
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public JsonRpcError Error { get; set; }
    }

    public class JsonRpcError
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public object Data { get; set; }
    }

    /// MCP tools/call result: a single text-content block whose `text` is a JSON string.
    public class ToolCallResult
    {
        [JsonProperty("content")] public ToolContent[] Content { get; set; }
        [JsonProperty("isError", NullValueHandling = NullValueHandling.Ignore)]
        public bool? IsError { get; set; }
    }

    public class ToolContent
    {
        [JsonProperty("type")] public string Type { get; set; } = "text";
        [JsonProperty("text")] public string Text { get; set; }
    }
}
