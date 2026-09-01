using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Discovery for the "which parameter IS classification here" question. The built-in
    /// classification fields the other tools probe by default (Assembly Code, OmniClass) are
    /// Revit's own — an office's NL-SfB or Uniclass value almost always lives in a differently
    /// named shared/project parameter instead, and there is otherwise no way for a caller to
    /// learn that name short of opening the model in Revit. This tool reports candidates with
    /// evidence (a populated count and a few sample values) rather than picking a winner — the
    /// role boundary (ROADMAP.md "Any classification crosswalk ... Mycelium Studio's private
    /// moat") means this connector never decides which candidate is authoritative.
    /// </summary>
    public sealed class GetClassificationSourcesTool : IPdraTool
    {
        public string Name        => "pdra_get_classification_sources";
        public string Description =>
            "Discover which parameter actually carries classification (NL-SfB, Uniclass, or an office's own " +
            "scheme) in THIS model, before guessing a name for classification_params on the other tools. " +
            "Samples elements (optionally scoped to category) and reports candidate parameters. By default: " +
            "text-valued type/instance parameters whose name looks classification-like (matches sfb, " +
            "uniclass, omniclass, uniformat, 'assembly code', or classification). Pass parameter_names[] to " +
            "check only those exact names instead. Pass all=true to instead list EVERY text-valued type " +
            "parameter regardless of name (instance parameters are skipped in this mode — too noisy at " +
            "instance scope). Each candidate carries level (type/instance), storage, populated (count out of " +
            "the sample with a real value), sample_values (up to 3, as evidence), and builtin (the matching " +
            "BuiltInParameter name, when it is one). This tool reports candidates — it does not decide which " +
            "one is authoritative; that judgment belongs to the caller.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["category"] = new JsonObject
                {
                    ["type"]        = "string",
                    ["description"] = "BuiltInCategory to sample, e.g. OST_Walls. Omit to sample across the whole document.",
                },
                ["sample"] = new JsonObject
                {
                    ["type"]        = "integer",
                    ["description"] = "Max elements to sample (default 500, max 5000).",
                },
                ["parameter_names"] = new JsonObject
                {
                    ["type"]        = "array",
                    ["items"]       = new JsonObject { ["type"] = "string" },
                    ["description"] = "Check only these exact parameter names instead of the name-pattern heuristic.",
                },
                ["all"] = new JsonObject
                {
                    ["type"]        = "boolean",
                    ["description"] = "Report every text-valued TYPE parameter, not just name-pattern matches. Default false.",
                },
            },
            ["additionalProperties"] = false,
        };

        // Case-insensitive substring match against the parameter's display name.
        private static readonly string[] NamePatterns =
            { "sfb", "uniclass", "omniclass", "uniformat", "assembly code", "assembly_code", "classification" };

        private enum ScanMode { Heuristic, Exact, AllTypeOnly }

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            var sample = 500;
            if (args.TryGetInt("sample", out var s)) sample = JsonHelpers.Clamp(s, 1, 5000);

            var exactNames = args.GetStringArray("parameter_names");
            bool all = args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("all", out var allEl) && allEl.ValueKind == JsonValueKind.True;

            IEnumerable<Element> query;
            if (args.TryGetString("category", out var catName))
            {
                if (!Enum.TryParse<BuiltInCategory>(catName, out var bic))
                    return ToolResult.Error($"Unknown BuiltInCategory '{catName}'.");
                query = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().Cast<Element>();
            }
            else
            {
                query = new FilteredElementCollector(doc).WhereElementIsNotElementType().Cast<Element>();
            }

            var elements = query.Take(sample).ToList();

            var mode = exactNames is not null ? ScanMode.Exact : (all ? ScanMode.AllTypeOnly : ScanMode.Heuristic);

            var agg = new Dictionary<(string Level, string Name), Candidate>();
            foreach (var el in elements)
            {
                var typeId   = el.GetTypeId();
                var typeElem = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;

                if (typeElem is not null) ScanParams(typeElem, "type", mode, exactNames, agg);
                if (mode != ScanMode.AllTypeOnly) ScanParams(el, "instance", mode, exactNames, agg);
            }

            var sources = new JsonArray();
            foreach (var c in agg.Values.OrderByDescending(c => c.Populated).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                sources.Add(new JsonObject
                {
                    ["parameter"]     = c.Name,
                    ["level"]         = c.Level,
                    ["storage"]       = c.Storage,
                    ["populated"]     = c.Populated,
                    ["sample_values"] = new JsonArray(c.SampleValues.Select(v => (JsonNode)v).ToArray()),
                    ["builtin"]       = c.Builtin,
                });
            }

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject
            {
                ["sampled"] = elements.Count,
                ["sources"] = sources,
            }));
        }

        private static void ScanParams(
            Element el, string level, ScanMode mode, List<string>? exactNames,
            Dictionary<(string, string), Candidate> agg)
        {
            foreach (Parameter p in el.Parameters)
            {
                var name = p.Definition?.Name;
                if (string.IsNullOrEmpty(name)) continue;

                switch (mode)
                {
                    case ScanMode.Exact:
                        if (!exactNames!.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;
                        break;
                    case ScanMode.Heuristic:
                        if (!NamePatterns.Any(pat => name.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                        break;
                    case ScanMode.AllTypeOnly:
                        if (p.StorageType != StorageType.String) continue;
                        break;
                }

                var key = (level, name);
                if (!agg.TryGetValue(key, out var c))
                {
                    c = new Candidate { Level = level, Name = name!, Storage = p.StorageType.ToString() };
                    if (p.Definition is InternalDefinition idef && idef.BuiltInParameter != BuiltInParameter.INVALID)
                        c.Builtin = idef.BuiltInParameter.ToString();
                    agg[key] = c;
                }

                var v = ElementContextReader.ReadParamValue(p);
                if (!string.IsNullOrEmpty(v))
                {
                    c.Populated++;
                    if (c.SampleValues.Count < 3 && !c.SampleValues.Contains(v))
                        c.SampleValues.Add(v);
                }
            }
        }

        private sealed class Candidate
        {
            public string Level = "";
            public string Name = "";
            public string? Storage;
            public string? Builtin;
            public int Populated;
            public List<string> SampleValues = new();
        }
    }
}
