using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Loam.Revit.Connector.Profiles;
using Loam.Revit.Connector.RevitBridge;
using Newtonsoft.Json.Linq;

namespace Loam.Revit.Connector.Mcp.Tools
{
    public class GetDoorRoomsTool : IMcpTool
    {
        private readonly RevitContext _ctx;
        private readonly Profile _profile;
        public GetDoorRoomsTool(RevitContext ctx, Profile profile) { _ctx = ctx; _profile = profile; }

        public string Description =>
            "Returns from/to rooms per door, plus clear-width param and type name (carries dm## and _mk tokens).";
        public object InputSchema => new
        {
            type = "object",
            required = new[] { "element_ids" },
            properties = new
            {
                element_ids = new { type = "array", items = new { type = "integer" } },
                scope_box_id = new { type = "integer" },
                limit = new { type = "integer", @default = 500 }
            }
        };

        public object Invoke(JObject args)
        {
            var rawIds = args["element_ids"]?.ToObject<long[]>() ?? System.Array.Empty<long>();
            int limit = args["limit"]?.Value<int>() ?? 500;

            return _ctx.Run(doc =>
            {
                var doors = new List<object>();
                int taken = 0;
                foreach (var rid in rawIds)
                {
                    if (taken >= limit) break;
                    var e = doc.GetElement(new ElementId((int)rid)) as FamilyInstance;
                    if (e == null) continue;

                    var (from, to) = ResolveRooms(e);
                    var typeName = ElementMapper.ResolveTypeName(e);
                    var row = new Dictionary<string, object>
                    {
                        ["unique_id"] = e.UniqueId,
                        ["id"] = e.Id.IntegerValue,
                        ["ifc_guid"] = ElementMapper.GetParam(e, "IfcGUID"),
                        ["type_name"] = typeName,
                        ["from_room"] = RoomDto(from),
                        ["to_room"] = RoomDto(to),
                    };

                    // Width parameter — first profile match wins, emitted under its source name.
                    foreach (var p in _profile.DoorClearWidthParams ?? new List<string>())
                    {
                        var v = ElementMapper.GetParamDouble(e, p);
                        if (v.HasValue) { row[p] = v.Value; break; }
                    }

                    doors.Add(row);
                    taken++;
                }
                return new { doors };
            });
        }

        private (Room from, Room to) ResolveRooms(FamilyInstance door)
        {
            // FromRoom/ToRoom on doors is phase-dependent.
            Room from = null, to = null;
            try { from = door.FromRoom; } catch { }
            try { to = door.ToRoom; } catch { }
            if (from == null && to == null)
            {
                var phases = new FilteredElementCollector(door.Document).OfClass(typeof(Phase)).Cast<Phase>().ToList();
                foreach (var ph in phases)
                {
                    try { from ??= door.get_FromRoom(ph); } catch { }
                    try { to   ??= door.get_ToRoom(ph);   } catch { }
                    if (from != null && to != null) break;
                }
            }
            return (from, to);
        }

        private object RoomDto(Room r)
        {
            if (r == null) return null;
            string function = null;
            foreach (var p in _profile.RoomFunctionParams ?? new List<string>())
            {
                var v = ElementMapper.GetParam(r, p);
                if (!string.IsNullOrWhiteSpace(v)) { function = v.Trim(); break; }
            }
            return new { function, name = r.Name };
        }
    }
}
