using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loam.Revit.Connector.ModelLogCapture;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// One of the model-log handoff plan's "on-demand detail" tools (docs/MODEL_LOG.md's
    /// "Tools: on-demand detail and acting on Revit" section) — the heavy data the log
    /// deliberately leaves out (full geometry, boundary lines, per-view visibility) fetched only
    /// when a caller actually needs it, for up to 50 elements at a time. Never logged; this tool
    /// exists BECAUSE the log doesn't carry this data.
    /// </summary>
    public sealed class GetElementDetailTool : IPdraTool
    {
        public string Name => "pdra_get_element_detail";
        public string Description =>
            "On-demand heavy detail for up to 50 elements (by unique_id), deliberately left out of " +
            "the model-log: geometry (triangulated faces, capped), boundaries (room/space boundary " +
            "segments and the bounding elements — Rooms/Spaces only), views (views/sheets where the " +
            "element is visible), live (the element's current state in the SAME shape as the log's " +
            "own `el` record, to confirm the log is fresh). Pass include[] to pick which of these to " +
            "compute — each is its own Revit API cost, so only ask for what you need.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        private const int MaxIds = 50;
        private const int MaxTrianglesPerElement = 2000;
        private const int MaxViewsScanned = 200;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["ids"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = $"UniqueIds to fetch detail for (max {MaxIds}).",
                },
                ["include"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "geometry", "boundaries", "views", "live" },
                    },
                    ["description"] = "Which detail groups to compute. Omit for all four.",
                },
            },
            ["required"] = new JsonArray { "ids" },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var doc = ctx.UiApp.ActiveUIDocument?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            var ids = args.GetStringArray("ids");
            if (ids is null || ids.Count == 0) return ToolResult.Error("ids is required (at least one UniqueId).");
            if (ids.Count > MaxIds) return ToolResult.Error($"At most {MaxIds} ids per call (got {ids.Count}).");

            var include = args.GetStringArray("include");
            bool Want(string key) => include is null || include.Contains(key, StringComparer.OrdinalIgnoreCase);

            var defaultPhase = ElementContextReader.DefaultPhase(doc, ctx.UiApp.ActiveUIDocument);
            var nodeIndex = Want("live") ? BuildNodeIndex(doc) : new Dictionary<ElementId, string>();
            var gridLines = Want("live") ? BuildGridLines(doc) : new List<(string, Line)>();

            var rows = new JsonArray();
            foreach (var uid in ids)
            {
                var el = doc.GetElement(uid);
                var row = new JsonObject { ["unique_id"] = uid };
                if (el is null)
                {
                    row["found"] = false;
                    rows.Add(row);
                    continue;
                }
                row["found"] = true;

                if (Want("geometry")) row["geometry"] = BuildGeometry(el);
                if (Want("boundaries")) row["boundaries"] = BuildBoundaries(el, defaultPhase);
                if (Want("views")) row["views"] = BuildVisibleIn(doc, el);
                if (Want("live"))
                {
                    void OnParamDef(Parameter p, bool isType) { /* not writing a pdef record here — read-only tool */ }
                    row["live"] = RecordBuilder.BuildElementFields(el, nodeIndex, gridLines, defaultPhase, OnParamDef);
                }

                rows.Add(row);
            }

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject { ["elements"] = rows }));
        }

        private static JsonObject? BuildGeometry(Element el)
        {
            try
            {
                var opts = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Medium };
                var geom = el.get_Geometry(opts);
                if (geom is null) return null;

                var faces = new JsonArray();
                var triangleBudget = MaxTrianglesPerElement;
                foreach (var obj in geom)
                {
                    if (triangleBudget <= 0) break;
                    if (obj is Solid solid)
                    {
                        foreach (Face face in solid.Faces)
                        {
                            if (triangleBudget <= 0) break;
                            var mesh = face.Triangulate();
                            if (mesh is null) continue;
                            var verts = new JsonArray();
                            foreach (var v in mesh.Vertices) verts.Add(new JsonArray { v.X, v.Y, v.Z });
                            var tris = new JsonArray();
                            for (var i = 0; i < mesh.NumTriangles && triangleBudget > 0; i++, triangleBudget--)
                            {
                                var t = mesh.get_Triangle(i);
                                tris.Add(new JsonArray { t.get_Index(0), t.get_Index(1), t.get_Index(2) });
                            }
                            faces.Add(new JsonObject { ["vertices"] = verts, ["triangles"] = tris });
                        }
                    }
                }
                return faces.Count > 0 ? new JsonObject { ["faces"] = faces, ["truncated"] = triangleBudget <= 0 } : null;
            }
            catch { return null; }
        }

        private static JsonObject? BuildBoundaries(Element el, Phase? phase)
        {
            if (el is not SpatialElement spatial) return null;
            try
            {
                var opts = new SpatialElementBoundaryOptions();
                var loops = spatial.GetBoundarySegments(opts);
                if (loops is null || loops.Count == 0) return null;

                var loopsArr = new JsonArray();
                foreach (var loop in loops)
                {
                    var segArr = new JsonArray();
                    foreach (var seg in loop)
                    {
                        var curve = seg.GetCurve();
                        var segRow = new JsonObject
                        {
                            ["start"] = new JsonArray { curve.GetEndPoint(0).X, curve.GetEndPoint(0).Y, curve.GetEndPoint(0).Z },
                            ["end"] = new JsonArray { curve.GetEndPoint(1).X, curve.GetEndPoint(1).Y, curve.GetEndPoint(1).Z },
                        };
                        var boundingId = seg.ElementId;
                        if (boundingId is not null && boundingId != ElementId.InvalidElementId)
                        {
                            var boundingEl = el.Document.GetElement(boundingId);
                            if (boundingEl is not null) segRow["boundingElement"] = boundingEl.UniqueId;
                        }
                        segArr.Add(segRow);
                    }
                    loopsArr.Add(segArr);
                }
                return new JsonObject { ["loops"] = loopsArr };
            }
            catch { return null; }
        }

        private static JsonObject? BuildVisibleIn(Document doc, Element el)
        {
            var sheets = new JsonArray();
            var views = new JsonArray();
            try
            {
                var allSheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
                var scanned = 0;
                foreach (var sheet in allSheets)
                {
                    foreach (var vid in sheet.GetAllPlacedViews())
                    {
                        if (scanned++ >= MaxViewsScanned) break;
                        if (doc.GetElement(vid) is not View view) continue;
                        bool visible;
                        try
                        {
                            visible = new FilteredElementCollector(doc, view.Id)
                                .WhereElementIsNotElementType()
                                .Where(e => e.Id == el.Id)
                                .Any();
                        }
                        catch { continue; /* view doesn't support element enumeration (e.g. schedules) */ }

                        if (!visible) continue;
                        views.Add(new JsonObject { ["unique_id"] = view.UniqueId, ["name"] = view.Name });
                        sheets.Add(new JsonObject { ["unique_id"] = sheet.UniqueId, ["number"] = sheet.SheetNumber });
                    }
                    if (scanned >= MaxViewsScanned) break;
                }
            }
            catch { }

            if (views.Count == 0 && sheets.Count == 0) return null;
            return new JsonObject { ["views"] = views, ["sheets"] = sheets, ["scanLimited"] = true };
        }

        private static Dictionary<ElementId, string> BuildNodeIndex(Document doc)
        {
            var index = new Dictionary<ElementId, string>();
            foreach (var lvl in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                index[lvl.Id] = RecordBuilder.NodeId(lvl.Id);
            var spaces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Concat(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType());
            foreach (var r in spaces) index[r.Id] = RecordBuilder.NodeId(r.Id);
            return index;
        }

        private static List<(string Name, Line Line)> BuildGridLines(Document doc)
        {
            var lines = new List<(string, Line)>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
                if (g.Curve is Line line) lines.Add((g.Name, line));
            return lines;
        }
    }
}
