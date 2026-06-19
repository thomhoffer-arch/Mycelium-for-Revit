using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    /// <summary>
    /// Resolves each door to its adjacent rooms — the "one piece of glue" for
    /// relational rules (e.g. clear-width vs adjacent-room function). Uses Revit's
    /// From/To Room when populated, else a geometric fallback (sample a point on
    /// each side of the door panel and ask which room encloses it). Returns the
    /// door's type name + requested door params and each room's number/function.
    /// </summary>
    public sealed class GetDoorRoomsTool : IPdraTool
    {
        public string Name        => "pdra_get_door_rooms";
        public string Description =>
            "For each door, resolve the rooms on either side (from_room / to_room) — the glue for relational " +
            "rules like clear-width vs adjacent-room function. Uses Revit From/To Room when set, else a " +
            "geometric fallback (a point each side of the door → enclosing room). Targets doors via " +
            "element_ids[] OR category (default OST_Doors) OR selection. door_params[] read flat onto each " +
            "door (default NLRS_C_breedte_01); room_params[] onto each room (default NLRS_C_ruimtefunctie, " +
            "gebruiksfunctie). type_name carries the door type token (e.g. dm###). Each row has from_room/" +
            "to_room {id, name, number, level_name, params} and resolution = from_to_room | geometric | none.";

        public Reversibility Reversibility => Reversibility.Reversible;
        public Verifiability Verifiability => Verifiability.Auto;

        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["element_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "Door element ids. Omit with category to use the current selection." },
                ["category"]    = new JsonObject { ["type"] = "string", ["description"] = "BuiltInCategory of the openings to resolve. Default OST_Doors." },
                ["view_id"]     = new JsonObject { ["type"] = "integer", ["description"] = "Limit a category query to this view." },
                ["door_params"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Parameter names to read flat onto each door. Default ['NLRS_C_breedte_01']." },
                ["room_params"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Parameter names to read onto each room's params{}. Default ['NLRS_C_ruimtefunctie','gebruiksfunctie']." },
                ["phase"]       = new JsonObject { ["type"] = "string", ["description"] = "Phase name for From/To Room and the geometric lookup. Default: the active view's phase, else the last phase." },
                ["phase_id"]    = new JsonObject { ["type"] = "integer", ["description"] = "Phase element id (alternative to phase)." },
                ["limit"]       = new JsonObject { ["type"] = "integer", ["description"] = "Max doors to return. Default 200." },
            },
            ["additionalProperties"] = false,
        };

        public ToolResult Run(ToolContext ctx, JsonElement args)
        {
            var uidoc = ctx.UiApp.ActiveUIDocument;
            var doc   = uidoc?.Document;
            if (doc is null) return ToolResult.Error("No active document.");

            var doorParams = ReadStrings(args, "door_params") ?? new List<string> { "NLRS_C_breedte_01" };
            var roomParams = ReadStrings(args, "room_params") ?? new List<string> { "NLRS_C_ruimtefunctie", "gebruiksfunctie" };

            var phase = ResolvePhase(doc, uidoc, args, out var phaseErr);
            if (phaseErr is not null) return ToolResult.Error(phaseErr);

            int limit = 200;
            if (args.TryGetProperty("limit", out var limEl) && limEl.ValueKind == JsonValueKind.Number)
                limit = JsonHelpers.Clamp(limEl.GetInt32(), 1, 2000);

            var doors = ResolveElements(uidoc, doc, args, defaultCategory: BuiltInCategory.OST_Doors, out var targErr);
            if (targErr is not null) return ToolResult.Error(targErr);

            var rows  = new JsonArray();
            int count = 0;
            foreach (var el in doors)
            {
                if (count >= limit) break;
                if (el is not FamilyInstance fi) continue;

                var typeId   = fi.GetTypeId();
                var typeElem = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;

                var row = new JsonObject
                {
                    ["id"]        = fi.Id.Value,
                    ["name"]      = fi.Name,
                    ["type_name"] = typeElem?.Name,
                    ["mark"]      = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                    ["host_id"]   = fi.Host?.Id.Value,
                };

                foreach (var pn in doorParams)
                {
                    var v = ReadParamValue(fi, pn) ?? ReadParamValue(typeElem, pn);
                    if (v is not null) row[pn] = v;
                }

                Room? fromR = TryFromRoom(fi, phase);
                Room? toR   = TryToRoom(fi, phase);
                string resolution = (fromR is not null || toR is not null) ? "from_to_room" : "none";

                if (fromR is null && toR is null)
                {
                    if (TryGeometricRooms(doc, fi, phase, out fromR, out toR) && (fromR is not null || toR is not null))
                        resolution = "geometric";
                }

                row["from_room"] = RoomNode(doc, fromR, roomParams);
                row["to_room"]   = RoomNode(doc, toR, roomParams);
                row["resolution"] = resolution;

                rows.Add(row);
                count++;
            }

            return ToolResult.Ok(JsonHelpers.Serialize(new JsonObject
            {
                ["count"]     = count,
                ["phase"]     = phase?.Name,
                ["elements"]  = rows,
            }));
        }

        // ── Room resolution ──────────────────────────────────────────────────────

        private static Room? TryFromRoom(FamilyInstance fi, Phase? phase)
        {
            if (phase is null) return null;
            try { return fi.get_FromRoom(phase); } catch { return null; }
        }

        private static Room? TryToRoom(FamilyInstance fi, Phase? phase)
        {
            if (phase is null) return null;
            try { return fi.get_ToRoom(phase); } catch { return null; }
        }

        /// <summary>Sample a point a little beyond each face of the door along its facing
        /// normal and ask which room encloses it.</summary>
        private static bool TryGeometricRooms(Document doc, FamilyInstance fi, Phase? phase, out Room? from, out Room? to)
        {
            from = null; to = null;
            if ((fi.Location as LocationPoint)?.Point is not XYZ pt) return false;

            var f  = fi.FacingOrientation;
            var fh = new XYZ(f.X, f.Y, 0);
            fh = fh.GetLength() > 1e-6 ? fh.Normalize() : XYZ.BasisY;

            double offset = 1.0; // ~305 mm beyond the door centre
            if (fi.Host is Wall w) { try { offset = w.Width / 2.0 + 0.5; } catch { } }

            from = RoomAt(doc, pt + fh.Multiply(offset), phase);
            to   = RoomAt(doc, pt - fh.Multiply(offset), phase);
            return true;
        }

        private static Room? RoomAt(Document doc, XYZ p, Phase? phase)
        {
            try { return (phase is not null ? doc.GetRoomAtPoint(p, phase) : doc.GetRoomAtPoint(p)) as Room; }
            catch { return null; }
        }

        private static JsonNode? RoomNode(Document doc, Room? r, List<string> roomParams)
        {
            if (r is null) return null;
            var node = new JsonObject
            {
                ["id"]         = r.Id.Value,
                ["name"]       = r.Name,
                ["number"]     = r.Number,
                ["level_name"] = (doc.GetElement(r.LevelId) as Level)?.Name,
            };
            JsonObject? p = null;
            foreach (var pn in roomParams)
            {
                var v = ReadParamValue(r, pn);
                if (v is null) continue;
                p ??= new JsonObject();
                p[pn] = v;
            }
            if (p is not null) node["params"] = p;
            return node;
        }

        // ── Helpers ────────────────────────────────────────────────────────────────

        private static string? ReadParamValue(Element? el, string name)
        {
            var p = el?.LookupParameter(name);
            if (p is null) return null;
            var vs = p.AsValueString();
            if (!string.IsNullOrEmpty(vs)) return vs;
            return p.StorageType switch
            {
                StorageType.String    => p.AsString(),
                StorageType.Integer   => p.AsInteger().ToString(),
                StorageType.Double    => p.AsDouble().ToString("0.######"),
                StorageType.ElementId => p.AsElementId().Value.ToString(),
                _                     => null,
            };
        }

        private static List<string>? ReadStrings(JsonElement args, string key)
        {
            if (!args.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array) return null;
            var list = el.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                         .Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return list.Count > 0 ? list : null;
        }

        private static Phase? ResolvePhase(Document doc, Autodesk.Revit.UI.UIDocument? uidoc, JsonElement args, out string? err)
        {
            err = null;
            if (args.TryGetLong("phase_id", out var pid))
            {
                if (doc.GetElement(new ElementId(pid)) is Phase ph) return ph;
                err = $"phase_id {pid} is not a Phase."; return null;
            }
            if (args.TryGetString("phase", out var pname) && !string.IsNullOrWhiteSpace(pname))
            {
                var ph = new FilteredElementCollector(doc).OfClass(typeof(Phase)).Cast<Phase>()
                    .FirstOrDefault(p => string.Equals(p.Name, pname, StringComparison.OrdinalIgnoreCase));
                if (ph is null) { err = $"No phase named '{pname}'."; return null; }
                return ph;
            }
            // Active view's phase, else the last document phase.
            var vp = uidoc?.ActiveView?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
            if (vp is { } id && id != ElementId.InvalidElementId && doc.GetElement(id) is Phase vph) return vph;
            var phases = doc.Phases;
            return phases.Size > 0 ? phases.get_Item(phases.Size - 1) : null;
        }

        private static IEnumerable<Element> ResolveElements(
            Autodesk.Revit.UI.UIDocument? uidoc, Document doc, JsonElement args,
            BuiltInCategory defaultCategory, out string? err)
        {
            err = null;
            if (args.TryGetProperty("element_ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                return idsEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number)
                    .Select(e => doc.GetElement(new ElementId(e.GetInt64()))).Where(e => e is not null)!;

            View? scopeView = null;
            if (args.TryGetLong("view_id", out var vid)) scopeView = doc.GetElement(new ElementId(vid)) as View;

            if (args.TryGetString("category", out var catName))
            {
                if (!Enum.TryParse<BuiltInCategory>(catName, out var bic)) { err = $"Unknown BuiltInCategory '{catName}'."; return Enumerable.Empty<Element>(); }
                return Collect(doc, bic, scopeView);
            }

            // Selection, else default category.
            var sel = uidoc?.Selection.GetElementIds();
            if (sel is { Count: > 0 })
                return sel.Select(id => doc.GetElement(id)).Where(e => e is not null)!;

            return Collect(doc, defaultCategory, scopeView);
        }

        private static IEnumerable<Element> Collect(Document doc, BuiltInCategory bic, View? scopeView)
            => (scopeView is not null
                    ? new FilteredElementCollector(doc, scopeView.Id)
                    : new FilteredElementCollector(doc))
                .OfCategory(bic).WhereElementIsNotElementType().Cast<Element>();
    }
}
