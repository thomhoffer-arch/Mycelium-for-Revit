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
            "Samples elements (optionally scoped to category — omit it to sample across the whole document) " +
            "and reports candidate parameters. scope controls what gets scanned: 'heuristic' (default) — " +
            "type+instance parameters whose name looks classification-like (matches sfb, uniclass, omniclass, " +
            "uniformat, 'assembly code', or classification); 'type' — every text-valued TYPE parameter, name " +
            "-agnostic (this is also what legacy all=true means, unchanged); 'instance' — every INSTANCE " +
            "parameter, name- and storage-agnostic (an office's classification parameter is often set per " +
            "instance, not per type — 'type'/'heuristic' cannot see it); 'all' — both levels, name- and " +
            "storage-agnostic. Pass parameter_names[] to check only those exact names instead of any scope. " +
            "Each candidate carries level (type/instance), storage (String/Integer/ElementId/…, reported as " +
            "evidence — 'instance'/'all' do not filter on it), populated (count out of the sample with a real " +
            "value), sample_values (up to 3, as evidence), and builtin (the matching BuiltInParameter name, " +
            "when it is one). This tool reports candidates — it does not decide which one is authoritative; " +
            "that judgment belongs to the caller.";

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
                    ["description"] = "Category to sample — the BuiltInCategory enum name (e.g. OST_Walls) or the " +
                                       "document's display name (e.g. Walls), enum name tried first. Omit to sample " +
                                       "across the whole document (spread across categories, not just the first " +
                                       "ones in document order).",
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
                    ["description"] = "Check only these exact parameter names instead of scope's name-pattern heuristic.",
                },
                ["scope"] = new JsonObject
                {
                    ["type"]        = "string",
                    ["enum"]        = new JsonArray { "heuristic", "type", "instance", "all" },
                    ["description"] = "'heuristic' (default): current name-pattern behavior, type+instance. " +
                                       "'type': every text-valued TYPE parameter, name-agnostic (same as legacy " +
                                       "all=true). 'instance': every INSTANCE parameter, name- and storage-agnostic " +
                                       "— use this when the classification value is set per instance rather than " +
                                       "per type. 'all': both levels, name- and storage-agnostic. Ignored when " +
                                       "parameter_names is set.",
                },
                ["all"] = new JsonObject
                {
                    ["type"]        = "boolean",
                    ["description"] = "Deprecated — use scope: \"type\" instead. Equivalent to scope: \"type\": " +
                                       "every text-valued TYPE parameter, not just name-pattern matches. Ignored " +
                                       "when scope is set. Default false.",
                },
            },
            ["additionalProperties"] = false,
        };

        // Case-insensitive substring match against the parameter's display name.
        private static readonly string[] NamePatterns =
            { "sfb", "uniclass", "omniclass", "uniformat", "assembly code", "assembly_code", "classification" };

        private enum ScanMode { Heuristic, Exact, TypeOnly, InstanceOnly, All }

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            var sample = 500;
            if (args.TryGetInt("sample", out var s)) sample = JsonHelpers.Clamp(s, 1, 5000);

            var exactNames = args.GetStringArray("parameter_names");

            ScanMode mode;
            if (exactNames is not null)
            {
                mode = ScanMode.Exact;
            }
            else if (args.TryGetString("scope", out var scopeArg))
            {
                switch (scopeArg.ToLowerInvariant())
                {
                    case "heuristic": mode = ScanMode.Heuristic;    break;
                    case "type":      mode = ScanMode.TypeOnly;     break;
                    case "instance":  mode = ScanMode.InstanceOnly; break;
                    case "all":       mode = ScanMode.All;          break;
                    default: return ToolResult.Error($"Unknown scope '{scopeArg}'. Use heuristic, type, instance, or all.");
                }
            }
            else
            {
                // Legacy arg, kept working exactly as before: every text-valued type parameter,
                // no name filter, type level only. This is now expressible as scope: "type" — the
                // two are intentionally the same code path so old callers see no behavior change.
                bool legacyAll = args.ValueKind == JsonValueKind.Object
                    && args.TryGetProperty("all", out var allEl) && allEl.ValueKind == JsonValueKind.True;
                mode = legacyAll ? ScanMode.TypeOnly : ScanMode.Heuristic;
            }

            List<Element> elements;
            if (args.TryGetString("category", out var catName))
            {
                if (!CategoryResolver.TryResolve(doc, catName, out var bic, out var catErr))
                    return ToolResult.Error(catErr!);
                elements = new FilteredElementCollector(doc).OfCategory(bic)
                    .WhereElementIsNotElementType().Cast<Element>().Take(sample).ToList();
            }
            else
            {
                elements = DistributedSample(doc, sample);
            }

            var agg = new Dictionary<(string Level, string Name), Candidate>();
            foreach (var el in elements)
            {
                var typeId   = el.GetTypeId();
                var typeElem = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;

                if (typeElem is not null && mode != ScanMode.InstanceOnly)
                    ScanParams(typeElem, "type", mode, exactNames, agg);
                if (mode != ScanMode.TypeOnly)
                    ScanParams(el, "instance", mode, exactNames, agg);
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

        /// <summary>
        /// Unscoped sampling used to be a plain <c>Take(sample)</c> over the document-order
        /// collector, which biased every unscoped discovery call toward whichever categories
        /// happen to sort first (typically walls/floors) and could hide a classification
        /// parameter that only lives on, say, doors or MEP equipment further down the model.
        /// This instead buckets elements by category while walking the collector (bounded by
        /// <see cref="MaxScanForSampling"/> so the walk stays lazy-bounded on very large
        /// documents) and then round-robins across the buckets, so the returned sample spreads
        /// across the categories actually present instead of favoring document order.
        /// </summary>
        private static List<Element> DistributedSample(Document doc, int sample)
        {
            var buckets = new Dictionary<string, List<Element>>();
            var scanned = 0;
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType().Cast<Element>())
            {
                var catKey = el.Category?.Name ?? "";
                if (!buckets.TryGetValue(catKey, out var bucket)) buckets[catKey] = bucket = new List<Element>();
                bucket.Add(el);
                if (++scanned >= MaxScanForSampling) break;
            }

            var bucketList = buckets.Values.Where(b => b.Count > 0).ToList();
            var result = new List<Element>(Math.Min(sample, scanned));
            var idx = 0;
            while (result.Count < sample)
            {
                bool any = false;
                foreach (var bucket in bucketList)
                {
                    if (idx >= bucket.Count) continue;
                    result.Add(bucket[idx]);
                    any = true;
                    if (result.Count >= sample) break;
                }
                if (!any) break;
                idx++;
            }
            return result;
        }

        // Upper bound on how many elements the unscoped collector walks while building category
        // buckets for DistributedSample, so a very large document still returns promptly instead
        // of materializing every element just to sample a few hundred of them.
        private const int MaxScanForSampling = 50_000;

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
                    case ScanMode.TypeOnly:
                        // Legacy all=true behavior: type-level, no name filter, string-only —
                        // preserved exactly so existing callers see no change.
                        if (p.StorageType != StorageType.String) continue;
                        break;
                    case ScanMode.InstanceOnly:
                    case ScanMode.All:
                        // Name- and storage-agnostic by design — the whole point of these modes
                        // is to surface a classification parameter regardless of its name or how
                        // Revit stores it (String, Integer, ElementId, …).
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
