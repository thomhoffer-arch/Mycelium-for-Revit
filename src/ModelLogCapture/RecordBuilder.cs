using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Loam.Revit.Connector.ModelLog;
using PDRA.Services.Ai.Tools.Queries;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Loam.Revit.Connector.ModelLogCapture
{
    /// <summary>
    /// Turns live Revit objects into the plain <see cref="JsonObject"/> field-groups
    /// docs/MODEL_LOG.md defines — the only place the model-log capture code touches the Revit
    /// API. Everything here is best-effort: a field that can't be resolved is left out (never a
    /// fabricated/blank value — the same "omit, don't blank" rule the existing PDRA tools
    /// follow), and no method here throws out of a Revit event handler (every Revit API call
    /// that can throw is individually wrapped, matching <c>ModelFacts.From</c>'s own style).
    ///
    /// NOTE ON VERIFICATION: several shapes below (the nearest-grid-intersection heuristic, the
    /// exact set of "type-driven" vs "instance" parameters Revit reports per category, and
    /// whether <c>DocumentChanged</c> reports other users' changes after a sync) are marked
    /// "NEEDS LIVE-REVIT CHECK" — the handoff plan's own "these can't be assumed" list. The
    /// reconcile pass (see <c>ModelLogService</c>) is what makes the log correct even if one of
    /// these heuristics is imperfect: it re-derives every hash from the live model on every
    /// open/sync, so a wrong guess here self-heals rather than silently drifting forever.
    /// </summary>
    public static class RecordBuilder
    {
        // ── Element filter (which elements get an `el` record at all) ──────────────

        // Category names confirmed as noise by the first real-model test (docs/MODEL_LOG.md's
        // "Review of the first real log" section): area boundaries, sun path, and a few others
        // whose CategoryType.Model + bracket/type filtering below might not catch on every Revit
        // language/version. Display names, so this is best-effort across locales — the type
        // checks below are the real defense (locale-independent).
        private static readonly HashSet<string> NoiseCategoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Area Boundary", "Sun Path", "Cameras", "Legend Components",
            "Work Plane Grid", "Space Type Settings",
        };

        /// <summary>Whether an element belongs in the log's `el` family at all — the fix for
        /// the first real-model test's #1 finding: a raw whole-document walk logged 838 area
        /// boundaries, 410 sketches, 243 sun path, 198 automatic dimensions and 158 views
        /// against only 121 actual walls. Filters to genuine building/MEP/furnishing elements:
        /// <see cref="CategoryType.Model"/> categories, never Revit's own bracketed internal
        /// categories (<c>"&lt;Sketch&gt;"</c> etc.), never rooms/spaces/areas (those are the
        /// spatial tree — logged as <c>node</c> records instead, by the caller's own separate
        /// pass), and never annotation/view/schedule objects that occasionally carry a
        /// CategoryType.Model category as a Revit quirk (checked by TYPE, not just category, so
        /// that quirk can't let noise back in).</summary>
        public static bool IsLoggableModelElement(Element el)
        {
            var cat = el.Category;
            if (cat is null) return false;
            if (string.IsNullOrEmpty(cat.Name) || cat.Name.StartsWith("<", StringComparison.Ordinal)) return false;
            if (cat.CategoryType != CategoryType.Model) return false;
            if (NoiseCategoryNames.Contains(cat.Name)) return false;

            if (el is SpatialElement) return false; // Room, Space, Area — belongs in `node`, not `el`

            if (el is View || el is ViewSheet || el is Viewport || el is Sketch || el is SketchPlane ||
                el is Dimension || el is IndependentTag || el is ScheduleSheetInstance ||
                el is AreaScheme || el is Material)
                return false;

            return true;
        }

        // ── Header ───────────────────────────────────────────────────────────────

        /// <summary>Model identity, producer/version, display units per spec, coordinates, and
        /// the field-role map — the first line of every segment. <paramref name="modelId"/> is
        /// the STABLE identity the log folder itself is named from
        /// (<see cref="Loam.Revit.Connector.ModelLogCapture.ModelLogService"/>'s own
        /// <c>ModelId</c> — the cloud/central-path <c>ModelInstanceId</c> when known, else the
        /// local file path), never the local file title — <c>facts.Model</c> differs per user
        /// (it can carry the Windows username) and must not be mistaken for the model's
        /// identity.</summary>
        public static JsonObject BuildHeader(
            Document doc, Loam.Revit.Connector.RevitBridge.ModelFacts facts, string producerVersion, string modelId)
        {
            var header = new JsonObject
            {
                ["title"] = facts.Model,
                ["modelId"] = modelId,
                ["project"] = facts.Project,
                ["worksharing"] = facts.Worksharing,
            };
            if (facts.CentralModelPath is not null) header["centralModelPath"] = facts.CentralModelPath;
            if (facts.CloudProjectGuid is not null) header["cloudProjectGuid"] = facts.CloudProjectGuid;
            if (facts.CloudModelGuid is not null) header["cloudModelGuid"] = facts.CloudModelGuid;
            if (facts.ModelInstanceId is not null) header["modelInstanceId"] = facts.ModelInstanceId;
            header["producer"] = "mycelium-revit-connector/" + producerVersion;
            try { header["revitVersion"] = doc.Application.VersionNumber; } catch { }

            header["units"] = BuildDisplayUnits(doc);
            header["coordinates"] = BuildCoordinates(doc);

            header["fieldRoles"] = new JsonObject
            {
                ["identity"] = new JsonArray { "id", "eid", "ifc" },
                ["handle"] = new JsonArray { "h", "grid" },
                ["location"] = new JsonArray { "loc", "bb", "pt" },
                ["type"] = new JsonArray { "type" },
                ["relation"] = new JsonArray { "rel", "mats" },
                ["sheet"] = new JsonArray { "sheet", "sheets" },
                ["quantity"] = new JsonArray { "q", "bb", "pt" },
                ["param"] = new JsonArray { "p" },
            };
            return header;
        }

        /// <summary>Every measurable spec's display unit, keyed by the spec's own
        /// <see cref="ForgeTypeId.TypeId"/> string (<c>UnitUtils.GetAllMeasurableSpecs()</c>) —
        /// the header field a reader uses to look up e.g. "3050 mm" for a `pdef` whose `spec` is
        /// `autodesk.spec.aec:length-2.0.0`, exactly as Revit displays it, without re-deriving
        /// Revit's own unit formatting rules. Was hard-coded to 4 specs; a real log review found
        /// 804,597 parameter values whose unit the reader couldn't resolve because most
        /// parameters carry a spec outside that short list.</summary>
        private static JsonObject BuildDisplayUnits(Document doc)
        {
            var units = new JsonObject();
            try
            {
                var fo = doc.GetUnits();
                foreach (var spec in UnitUtils.GetAllMeasurableSpecs())
                {
                    try { units[spec.TypeId] = fo.GetFormatOptions(spec).GetUnitTypeId().TypeId; }
                    catch { /* spec not set in this document/Revit version — omit */ }
                }
            }
            catch { }
            return units;
        }

        /// <summary>Project base point, survey point, true north angle and the shared-
        /// coordinate transform — so a reader can place the internal-unit coordinates every
        /// element/grid/sheet record carries into real-world / shared coordinates without
        /// re-deriving Revit's own base-point math.</summary>
        private static JsonObject BuildCoordinates(Document doc)
        {
            var coords = new JsonObject();
            try
            {
                var basePoint = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ProjectBasePoint)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault();
                if (basePoint is not null)
                {
                    var ns = basePoint.get_Parameter(BuiltInParameter.BASEPOINT_EASTWEST_PARAM)?.AsDouble();
                    var ew = basePoint.get_Parameter(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM)?.AsDouble();
                    var elev = basePoint.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM)?.AsDouble();
                    var angle = basePoint.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM)?.AsDouble();
                    if (ns is not null || ew is not null || elev is not null)
                        coords["projectBasePoint"] = new JsonObject { ["e"] = ew, ["n"] = ns, ["elev"] = elev };
                    if (angle is not null) coords["trueNorthAngleRad"] = angle;
                }

                var surveyPoint = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_SharedBasePoint)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault();
                if (surveyPoint is not null)
                {
                    var ns = surveyPoint.get_Parameter(BuiltInParameter.BASEPOINT_EASTWEST_PARAM)?.AsDouble();
                    var ew = surveyPoint.get_Parameter(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM)?.AsDouble();
                    var elev = surveyPoint.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM)?.AsDouble();
                    if (ns is not null || ew is not null || elev is not null)
                        coords["surveyPoint"] = new JsonObject { ["e"] = ew, ["n"] = ns, ["elev"] = elev };
                }

                // The doc comment above has always promised this — it just never actually
                // computed it. ActiveProjectLocation.GetTotalTransform() is what actually maps
                // this model's internal coordinates into shared coordinates (project base
                // point/angle above describe survey placement; this is the transform a linked
                // model or another document's shared coordinates would use).
                var transform = doc.ActiveProjectLocation?.GetTotalTransform();
                if (transform is not null)
                {
                    coords["sharedTransform"] = new JsonObject
                    {
                        ["origin"] = ToArray(transform.Origin),
                        ["basisX"] = ToArray(transform.BasisX),
                        ["basisY"] = ToArray(transform.BasisY),
                        ["basisZ"] = ToArray(transform.BasisZ),
                    };
                }
            }
            catch { }
            return coords;
        }

        // ── Project information ─────────────────────────────────────────────────

        public static JsonObject BuildProject(Document doc)
        {
            var fields = new JsonObject();
            try
            {
                var pi = doc.ProjectInformation;
                if (pi is null) return fields;
                if (!string.IsNullOrEmpty(pi.Number)) fields["number"] = pi.Number;
                if (!string.IsNullOrEmpty(pi.Name)) fields["name"] = pi.Name;
                if (!string.IsNullOrEmpty(pi.ClientName)) fields["client"] = pi.ClientName;
                if (!string.IsNullOrEmpty(pi.Address)) fields["address"] = pi.Address;
                if (!string.IsNullOrEmpty(pi.Status)) fields["status"] = pi.Status;

                var p = new JsonObject();
                foreach (Parameter param in pi.Parameters)
                {
                    if (!param.HasValue) continue;
                    var v = ElementContextReader.ReadParamValue(param);
                    if (v is null) continue;
                    p[param.Definition.Name] = v;
                }
                if (p.Count > 0) fields["params"] = p;
            }
            catch { }
            return fields;
        }

        // ── Category (cat) ──────────────────────────────────────────────────────

        /// <summary>Canonical cat-record id: <c>"c:" + display name</c> — matches the reference
        /// form <c>el.cat</c> carries (docs/MODEL_LOG.md's worked example: <c>"cat":"c:Walls"</c>).</summary>
        public static string CategoryId(Category cat) => "c:" + cat.Name;

        public static JsonObject BuildCategory(Category cat)
        {
            var fields = new JsonObject { ["name"] = cat.Name };
            var bic = CategoryResolver.CategoryId(cat);
            if (bic is not null) fields["builtInCategory"] = bic;
            try { fields["discipline"] = cat.CategoryType.ToString(); } catch { }
            return fields;
        }

        // ── Parameter definitions (pdef) ────────────────────────────────────────

        /// <summary>The pdef id scheme docs/MODEL_LOG.md defines: <c>builtin:&lt;BuiltInParameter&gt;</c>,
        /// <c>shared:&lt;GUID&gt;</c>, or the project parameter's own definition GUID/id, prefixed
        /// <c>project:</c> for the same "namespaced, never ambiguous" reason. Returns null when
        /// the parameter carries no stable identity to key a definition record on (rare — an
        /// unbound/legacy parameter) — the caller then omits it from <c>p</c> rather than invent
        /// an id.</summary>
        public static string? ParamDefId(Parameter p)
        {
            try
            {
                if (p.Definition is InternalDefinition idef && idef.BuiltInParameter != BuiltInParameter.INVALID)
                    return "builtin:" + idef.BuiltInParameter;
            }
            catch { }
            try
            {
                if (p.IsShared) return "shared:" + p.GUID;
            }
            catch { }
            try
            {
                if (p.Definition is InternalDefinition idef2)
                    return "project:" + idef2.Id.Value.ToString(CultureInfo.InvariantCulture);
            }
            catch { }
            return null;
        }

        public static JsonObject BuildParamDef(Parameter p, bool isTypeParam)
        {
            var fields = new JsonObject
            {
                ["name"] = p.Definition?.Name,
                ["storage"] = p.StorageType.ToString().ToLowerInvariant(),
                ["scope"] = isTypeParam ? "type" : "instance",
            };
            try { fields["group"] = LabelUtils.GetLabelForGroup(p.Definition.GetGroupTypeId()); } catch { }
            // The parameter's SPEC (e.g. length, area) — not its unit (p.GetUnitTypeId(), e.g.
            // millimeters). A reader looks the spec up in header.units to find the display unit;
            // GetUnitTypeId() would never match a header.units key at all (a real log review
            // found 804,597 param values whose unit the reader couldn't resolve because of this).
            // Non-measurable specs (strings, yes/no, …) get one too — a reader that doesn't
            // recognize them just won't find a matching header.units entry, which is correct.
            try
            {
                var spec = p.Definition?.GetDataType();
                if (spec is not null && !spec.Empty()) fields["spec"] = spec.TypeId;
            }
            catch { }
            return fields;
        }

        // ── Spatial tree (node) — levels, rooms/spaces as IFC-shaped tree entries ──

        public static string NodeId(ElementId id) => "n:" + id.Value.ToString(CultureInfo.InvariantCulture);

        public static JsonObject BuildLevelNode(Level level)
        {
            return new JsonObject
            {
                ["level"] = "storey",
                ["name"] = level.Name,
                ["elevation"] = level.Elevation,
            };
        }

        public static JsonObject BuildSpaceNode(Element roomOrSpace, ElementId? parentLevelId)
        {
            var fields = new JsonObject
            {
                ["level"] = roomOrSpace.Category?.Id.Value == (long)BuiltInCategory.OST_MEPSpaces ? "zone" : "space",
                ["name"] = roomOrSpace.Name,
            };
            if (parentLevelId is not null && parentLevelId != ElementId.InvalidElementId)
                fields["parent"] = NodeId(parentLevelId);
            try
            {
                var number = roomOrSpace.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                if (!string.IsNullOrEmpty(number)) fields["number"] = number;
            }
            catch { }
            return fields;
        }

        // ── Grids ────────────────────────────────────────────────────────────────

        public static JsonObject BuildGrid(Grid grid)
        {
            var fields = new JsonObject { ["name"] = grid.Name };
            try
            {
                if (grid.Curve is Line line)
                {
                    fields["line"] = new JsonObject
                    {
                        ["start"] = ToArray(line.GetEndPoint(0)),
                        ["end"] = ToArray(line.GetEndPoint(1)),
                    };
                }
            }
            catch { }
            return fields;
        }

        /// <summary>Best-effort "nearest grid intersection" label (e.g. "C/4") for an element's
        /// location point, from the already-loaded set of grids. NEEDS LIVE-REVIT CHECK — this
        /// is a straight-line nearest-pair heuristic (closest grid running roughly one axis +
        /// closest running the other), not Revit's own (unpublished) grid-bubble logic; good
        /// enough to be useful, not guaranteed to match what a drafter would call the location by
        /// eye on an irregular/curved/radial grid.</summary>
        public static string? NearestGridIntersection(XYZ point, IReadOnlyList<(string Name, Line Line)> grids)
        {
            if (grids.Count == 0) return null;

            string? bestVertical = null;
            var bestVerticalDist = double.MaxValue;
            string? bestHorizontal = null;
            var bestHorizontalDist = double.MaxValue;

            foreach (var (name, line) in grids)
            {
                var dir = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
                var isVertical = Math.Abs(dir.Y) > Math.Abs(dir.X); // runs mostly north-south
                var result = line.Project(point);
                if (result is null) continue;
                var dist = result.Distance;

                if (isVertical && dist < bestVerticalDist) { bestVerticalDist = dist; bestVertical = name; }
                else if (!isVertical && dist < bestHorizontalDist) { bestHorizontalDist = dist; bestHorizontal = name; }
            }

            if (bestVertical is not null && bestHorizontal is not null) return $"{bestVertical}/{bestHorizontal}";
            return bestVertical ?? bestHorizontal;
        }

        // ── Materials ────────────────────────────────────────────────────────────

        public static string MaterialId(ElementId id) => "m:" + id.Value.ToString(CultureInfo.InvariantCulture);

        public static JsonObject BuildMaterial(Material mat)
        {
            var fields = new JsonObject { ["name"] = mat.Name };
            try { fields["class"] = mat.MaterialClass; } catch { }
            var p = new JsonObject();
            foreach (Parameter param in mat.Parameters)
            {
                if (!param.HasValue) continue;
                var v = ElementContextReader.ReadParamValue(param);
                if (v is null) continue;
                p[param.Definition.Name] = v;
            }
            if (p.Count > 0) fields["params"] = p;
            return fields;
        }

        // ── Types ────────────────────────────────────────────────────────────────

        public static string TypeId(ElementId id) => "t:" + id.Value.ToString(CultureInfo.InvariantCulture);

        public static JsonObject BuildType(ElementType type, Action<Parameter, bool> onParam)
        {
            var fields = new JsonObject { ["name"] = type.Name };
            var catId = type.Category is not null ? CategoryId(type.Category) : null;
            if (catId is not null) fields["cat"] = catId;
            try { if (!string.IsNullOrEmpty(type.FamilyName)) fields["fam"] = type.FamilyName; } catch { }

            var p = new JsonObject();
            foreach (Parameter param in type.Parameters)
            {
                if (!param.HasValue) continue;
                onParam(param, true);
                var id = ParamDefId(param);
                if (id is null) continue;
                p[id] = ElementContextReader.ReadParamTyped(param)["value"]?.DeepClone();
            }
            if (p.Count > 0) fields["p"] = p;
            return fields;
        }

        // ── Sheets: which sheets show/tag each element ──────────────────────────

        /// <summary>Element id → the sheet numbers of every sheet a tag on that element is
        /// placed on, built once per snapshot/reconcile pass (a reverse index over every
        /// <see cref="IndependentTag"/> in the document is far cheaper than checking, per
        /// element, which of the document's views/sheets shows it). The first real-model test's
        /// fix #7: "which sheets show or tag the element".</summary>
        public static Dictionary<ElementId, List<string>> BuildTaggedSheetIndex(Document doc)
        {
            var viewToSheet = new Dictionary<ElementId, string>();
            foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
            {
                try
                {
                    foreach (var vid in sheet.GetAllPlacedViews()) viewToSheet[vid] = sheet.SheetNumber;
                }
                catch { }
            }

            var index = new Dictionary<ElementId, List<string>>();
            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)))
            {
                ElementId viewId;
                try { viewId = tag.OwnerViewId; } catch { continue; }
                if (!viewToSheet.TryGetValue(viewId, out var sheetNum)) continue;

                IEnumerable<ElementId> taggedIds;
                try { taggedIds = tag.GetTaggedLocalElementIds(); } catch { continue; }
                foreach (var tid in taggedIds)
                {
                    if (!index.TryGetValue(tid, out var list)) index[tid] = list = new List<string>();
                    if (!list.Contains(sheetNum)) list.Add(sheetNum);
                }
            }
            return index;
        }

        /// <summary>Reverse index: host ElementId → UniqueIds of every <see cref="FamilyInstance"/>
        /// it hosts — built once per pass (like <see cref="BuildTaggedSheetIndex"/>), so `rel.hosted`
        /// doesn't cost a per-element document walk. <c>FamilyInstance.Host</c> is the only
        /// "hosted by" relationship the Revit API exposes this directly; a wall containing a room
        /// isn't a host/hosted relationship in this sense (that's `loc.space`).</summary>
        public static Dictionary<ElementId, List<string>> BuildHostedIndex(Document doc)
        {
            var index = new Dictionary<ElementId, List<string>>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
            {
                Element? host;
                try { host = fi.Host; } catch { continue; }
                if (host is null) continue;
                if (!index.TryGetValue(host.Id, out var list)) index[host.Id] = list = new List<string>();
                list.Add(fi.UniqueId);
            }
            return index;
        }

        // ── Elements (el) ────────────────────────────────────────────────────────

        /// <summary>Builds the full field-group set for one element (docs/MODEL_LOG.md's
        /// "What goes into each element record" table). <paramref name="onParamDef"/> is called
        /// once per instance parameter encountered (param, isTypeParam: false) so the caller can
        /// lazily emit a <c>pdef</c> record the first time each parameter id is seen, without
        /// this method knowing anything about the writer/hash-cache. <paramref
        /// name="taggedSheets"/> (optional — see <see cref="BuildTaggedSheetIndex"/>) supplies
        /// <c>sheets</c>: the sheet numbers of every sheet a tag on this element is placed
        /// on.</summary>
        public static JsonObject BuildElementFields(
            Element el,
            IReadOnlyDictionary<ElementId, string> nodeIdByLevelOrSpace,
            IReadOnlyList<(string Name, Line Line)> grids,
            Phase? defaultPhase,
            Action<Parameter, bool> onParamDef,
            IReadOnlyDictionary<ElementId, List<string>>? taggedSheets = null,
            IReadOnlyDictionary<ElementId, List<string>>? hostedIndex = null)
        {
            var fields = new JsonObject { ["eid"] = el.Id.Value };

            var ifc = BuildIfcRef(el);
            if (ifc is not null) fields["ifc"] = ifc;

            if (el.Category is not null) fields["cat"] = CategoryId(el.Category);
            try { if (el is FamilyInstance fi && !string.IsNullOrEmpty(fi.Symbol?.FamilyName)) fields["fam"] = fi.Symbol.FamilyName; } catch { }

            var (typeIdVal, _) = ElementContextReader.ResolveType(el);
            if (typeIdVal is not null) fields["type"] = TypeId(new ElementId(typeIdVal.Value));

            var h = new JsonObject();
            if (ElementContextReader.ResolveMark(el) is { Length: > 0 } mark) h["mark"] = mark;
            try
            {
                var typeMark = (el.Document.GetElement(el.GetTypeId()))?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString();
                if (!string.IsNullOrEmpty(typeMark)) h["typeMark"] = typeMark;
            }
            catch { }
            try
            {
                var catId = el.Category?.Id.Value;
                if (catId == (long)BuiltInCategory.OST_Rooms || catId == (long)BuiltInCategory.OST_MEPSpaces)
                {
                    var number = el.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                    if (!string.IsNullOrEmpty(number)) h["number"] = number;
                    if (!string.IsNullOrEmpty(el.Name)) h["name"] = el.Name;
                }
            }
            catch { }
            if (h.Count > 0) fields["h"] = h;

            // Computed once and reused below (room lookup's curve/point-less fallback, and the
            // element's own `bb` field) — get_BoundingBox(null) used to run twice per element.
            BoundingBoxXYZ? bb;
            try { bb = el.get_BoundingBox(null); } catch { bb = null; }

            var loc = BuildLocationRef(el, nodeIdByLevelOrSpace, defaultPhase, bb);
            if (loc is not null) fields["loc"] = loc;

            try
            {
                var pt = LocationPointOf(el);
                if (pt is not null && grids.Count > 0)
                {
                    var g = NearestGridIntersection(pt, grids);
                    if (g is not null) fields["grid"] = g;
                }
            }
            catch { }

            var rel = BuildRelations(el, defaultPhase, hostedIndex);
            if (rel is not null) fields["rel"] = rel;

            var q = BuildQuantities(el);
            if (q is not null) fields["q"] = q;

            var bbOrPt = BuildGeometryRef(el, bb);
            if (bbOrPt is not null)
                foreach (var kv in bbOrPt) fields[kv.Key] = kv.Value?.DeepClone();

            var mats = BuildMaterials(el);
            if (mats is not null) fields["mats"] = mats;

            if (taggedSheets is not null && taggedSheets.TryGetValue(el.Id, out var sheetNums) && sheetNums.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var s in sheetNums) arr.Add(s);
                fields["sheets"] = arr;
            }

            var p = BuildInstanceParams(el, onParamDef);
            if (p is not null) fields["p"] = p;

            return fields;
        }

        /// <summary>IFC GlobalId — an extra identifier, not a conversion: this never turns the
        /// model into IFC, it just names the element the way ClashControl, BCF issues, IFC
        /// exports and emails already do, so Loam can link those back to the right Revit element
        /// without anyone re-exporting anything. The stored <c>IFC_GUID</c> parameter (what
        /// Revit's own IFC exporter actually wrote, when "Store IFC GUID" was enabled for a past
        /// export) is authoritative when present; otherwise the value is computed from
        /// <see cref="Element.UniqueId"/> via <see cref="IfcGuid.FromRevitUniqueId"/> and marked
        /// <c>derived: true</c> so a reader knows it hasn't been confirmed against an actual IFC
        /// export of this model. NEEDS LIVE-REVIT CHECK: export a small IFC from Revit with
        /// "Store IFC GUID" on and confirm the derived values here match the export's GlobalIds
        /// for elements that have never been exported before (so `IFC_GUID` was empty until that
        /// export set it).</summary>
        private static JsonObject? BuildIfcRef(Element el)
        {
            try
            {
                var stored = el.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
                if (!string.IsNullOrEmpty(stored)) return new JsonObject { ["guid"] = stored };
            }
            catch { }

            string? derived;
            try { derived = IfcGuid.FromRevitUniqueId(el.UniqueId); }
            catch { derived = null; }
            return derived is null ? null : new JsonObject { ["guid"] = derived, ["derived"] = true };
        }

        private static JsonObject? BuildLocationRef(
            Element el, IReadOnlyDictionary<ElementId, string> nodeIdByLevelOrSpace, Phase? defaultPhase, BoundingBoxXYZ? bb)
        {
            var loc = new JsonObject();
            try
            {
                var lvlId = el.LevelId;
                if (lvlId != ElementId.InvalidElementId && nodeIdByLevelOrSpace.TryGetValue(lvlId, out var lvlNode))
                    loc["storey"] = lvlNode;
            }
            catch { }

            // Only the room's id is needed here (to look up its own `node` record) — the lean
            // ResolveRoomId, not ResolveRoom's full name/number/level (built only to be thrown
            // away here), and reuses the caller's already-computed bbox instead of a second
            // get_BoundingBox(null) for a curve-based/no-location element.
            var roomElId = ElementContextReader.ResolveRoomId(el, defaultPhase, bb);
            if (roomElId is not null && nodeIdByLevelOrSpace.TryGetValue(roomElId, out var spaceNode))
                loc["space"] = spaceNode;

            return loc.Count > 0 ? loc : null;
        }

        private static JsonObject? BuildRelations(Element el, Phase? phase, IReadOnlyDictionary<ElementId, List<string>>? hostedIndex)
        {
            var rel = new JsonObject();
            try
            {
                if (el is FamilyInstance fi && fi.Host is not null)
                    rel["host"] = fi.Host.UniqueId;
            }
            catch { }

            // The reverse of `host` above — everything hosted BY this element (e.g. a wall's
            // hosted doors/windows). Skipped when the caller didn't build the index (rare — only
            // the very first change-capture batch of a session, before any snapshot/reconcile has
            // run yet — see BuildHostedIndex's own callers).
            if (hostedIndex is not null && hostedIndex.TryGetValue(el.Id, out var hostedIds) && hostedIds.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var h in hostedIds) arr.Add(h);
                rel["hosted"] = arr;
            }

            // Room from/to — "anything between two spaces" (the first real-model test's fix
            // #3): doors are the common case, but get_FromRoom/get_ToRoom apply to any
            // FamilyInstance Revit considers room-bounding-adjacent. Referenced by NODE id (the
            // room is logged as a `node` record, never as `el` — see IsLoggableModelElement),
            // matching `loc.storey`/`loc.space`'s own reference convention.
            try
            {
                if (el is FamilyInstance fi2 && phase is not null)
                {
                    Room? from = null, to = null;
                    try { from = fi2.get_FromRoom(phase); } catch { }
                    try { to = fi2.get_ToRoom(phase); } catch { }
                    if (from is not null) rel["roomFrom"] = NodeId(from.Id);
                    if (to is not null) rel["roomTo"] = NodeId(to.Id);
                }
            }
            catch { }

            // MEP system membership and connected elements — via the element's own connectors
            // (ducts/pipes/cable trays/conduits expose ConnectorManager directly; equipment/
            // fittings/fixtures expose it through FamilyInstance.MEPModel).
            try
            {
                ConnectorManager? cm = el is MEPCurve mc ? mc.ConnectorManager
                    : (el as FamilyInstance)?.MEPModel?.ConnectorManager;
                if (cm is not null)
                {
                    var systems = new List<string>();
                    var connected = new List<string>();
                    // Fully qualified: "Connector" is ALSO a namespace somewhere in this
                    // Revit API version's assembly graph, and the bare name resolves to that
                    // namespace instead of Autodesk.Revit.DB.Connector — caught by the Build
                    // workflow's real compile check (net48/Revit 2024 API packages).
                    foreach (Autodesk.Revit.DB.Connector c in cm.Connectors)
                    {
                        try
                        {
                            var sys = c.MEPSystem;
                            if (sys is not null && !string.IsNullOrEmpty(sys.Name) && !systems.Contains(sys.Name))
                                systems.Add(sys.Name);
                        }
                        catch { }
                        try
                        {
                            foreach (Autodesk.Revit.DB.Connector other in c.AllRefs)
                            {
                                if (other?.Owner is null || other.Owner.Id == el.Id) continue;
                                var uid = other.Owner.UniqueId;
                                if (!connected.Contains(uid)) connected.Add(uid);
                            }
                        }
                        catch { }
                    }
                    if (systems.Count > 0)
                    {
                        var arr = new JsonArray();
                        foreach (var s in systems) arr.Add(s);
                        rel["mepSystems"] = arr;
                    }
                    if (connected.Count > 0)
                    {
                        var arr = new JsonArray();
                        foreach (var c in connected) arr.Add(c);
                        rel["connected"] = arr;
                    }
                }
            }
            catch { }

            try
            {
                var group = el.GroupId;
                if (group != ElementId.InvalidElementId) rel["group"] = group.Value;
            }
            catch { }

            try
            {
                var assembly = el.AssemblyInstanceId;
                if (assembly != ElementId.InvalidElementId) rel["assembly"] = assembly.Value;
            }
            catch { }

            try
            {
                var opt = ElementContextReader.ResolveDesignOption(el);
                if (opt is not null) rel["designOption"] = opt["name"]?.GetValue<string>();
            }
            catch { }

            try
            {
                var wsParam = el.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (wsParam is not null && el.Document.IsWorkshared)
                {
                    var wsTable = el.Document.GetWorksetTable();
                    var wsId = new WorksetId(wsParam.AsInteger());
                    var ws = wsTable.GetWorkset(wsId);
                    if (ws is not null) rel["workset"] = ws.Name;
                }
            }
            catch { }

            try
            {
                var createdId = el.CreatedPhaseId;
                if (createdId != ElementId.InvalidElementId && el.Document.GetElement(createdId) is Phase created)
                    rel["phaseCreated"] = created.Name;
                var demolishedId = el.DemolishedPhaseId;
                if (demolishedId != ElementId.InvalidElementId && el.Document.GetElement(demolishedId) is Phase demo)
                    rel["phaseDemolished"] = demo.Name;
            }
            catch { }

            return rel.Count > 0 ? rel : null;
        }

        // Resolved by NAME at runtime (Enum.TryParse), not referenced as compile-time
        // BuiltInParameter members — mirrors ElementContextReader.ResolveBips' own defensive
        // pattern, since which of these apply varies per category (a wall's own length lives on
        // a different BIP than a door's own width) and this repo's two build targets (Revit
        // 2024 vs 2025/2026) don't guarantee every name below exists on every category. A
        // caller-named quantity that doesn't parse, or isn't set on this element, simply drops
        // out — never a fabricated 0. NEEDS LIVE-REVIT CHECK: confirm the right BIP per category
        // against real models (see the handoff's own verification checklist).
        private static readonly (string Key, string[] Bips)[] QuantityCandidates =
        {
            ("length", new[] { "CURVE_ELEM_LENGTH", "INSTANCE_LENGTH_PARAM" }),
            ("width", new[] { "FURNITURE_WIDTH", "DOOR_WIDTH", "WINDOW_WIDTH" }),
            ("height", new[] { "INSTANCE_HEIGHT_PARAM", "DOOR_HEIGHT", "WINDOW_HEIGHT", "WALL_USER_HEIGHT_PARAM" }),
            ("area", new[] { "HOST_AREA_COMPUTED", "ROOM_AREA" }),
            ("volume", new[] { "HOST_VOLUME_COMPUTED", "ROOM_VOLUME" }),
            ("perimeter", new[] { "HOST_PERIMETER_COMPUTED", "ROOM_PERIMETER" }),
            // Walls expose WALL_ATTR_WIDTH_PARAM on the instance already (mirrors Wall.Width);
            // floors/roofs/ceilings carry their own thickness BIP the same way — resolved by
            // name exactly like every other quantity above, no type-vs-instance special case.
            ("thickness", new[] { "WALL_ATTR_WIDTH_PARAM", "FLOOR_ATTR_THICKNESS_PARAM", "ROOF_ATTR_THICKNESS_PARAM", "CEILING_THICKNESS" }),
        };

        private static JsonObject? BuildQuantities(Element el)
        {
            var q = new JsonObject();
            foreach (var (key, bipNames) in QuantityCandidates)
            {
                foreach (var name in bipNames)
                {
                    if (!Enum.TryParse<BuiltInParameter>(name, out var bip)) continue;
                    Parameter? p;
                    try { p = el.get_Parameter(bip); } catch { p = null; }
                    if (p is null || !p.HasValue || p.StorageType != StorageType.Double) continue;
                    q[key] = p.AsDouble();
                    break;
                }
            }
            return q.Count > 0 ? q : null;
        }

        private static XYZ? LocationPointOf(Element el)
        {
            if (el.Location is LocationPoint lp) return lp.Point;
            if (el.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
            return null;
        }

        private static Dictionary<string, JsonNode?>? BuildGeometryRef(Element el, BoundingBoxXYZ? bb)
        {
            var result = new Dictionary<string, JsonNode?>();
            if (bb is not null)
                result["bb"] = new JsonArray { ToArray(bb.Min), ToArray(bb.Max) };

            try
            {
                if (el.Location is LocationPoint lp)
                    result["pt"] = ToArray(lp.Point);
                else if (el.Location is LocationCurve lc)
                    result["pt"] = new JsonObject
                    {
                        ["start"] = ToArray(lc.Curve.GetEndPoint(0)),
                        ["end"] = ToArray(lc.Curve.GetEndPoint(1)),
                    };
            }
            catch { }

            return result.Count > 0 ? result : null;
        }

        private static JsonArray? BuildMaterials(Element el)
        {
            try
            {
                var ids = el.GetMaterialIds(false);
                if (ids is null || ids.Count == 0) return null;
                var arr = new JsonArray();
                foreach (var id in ids)
                {
                    double area = 0, volume = 0;
                    try { area = el.GetMaterialArea(id, false); } catch { }
                    try { volume = el.GetMaterialVolume(id); } catch { }
                    arr.Add(new JsonArray { MaterialId(id), area, volume });
                }
                return arr.Count > 0 ? arr : null;
            }
            catch { return null; }
        }

        private static JsonObject? BuildInstanceParams(Element el, Action<Parameter, bool> onParamDef)
        {
            var p = new JsonObject();
            try
            {
                foreach (Parameter param in el.Parameters)
                {
                    if (!param.HasValue) continue;
                    onParamDef(param, false);
                    var id = ParamDefId(param);
                    if (id is null) continue;
                    var typed = ElementContextReader.ReadParamTyped(param);
                    p[id] = typed["value"]?.DeepClone();
                }
            }
            catch { }
            return p.Count > 0 ? p : null;
        }

        // ── Sheets, revisions, links ─────────────────────────────────────────────

        /// <param name="sheetElements">Optional (see <see cref="BuildSheetElementIndex"/>) —
        /// UniqueIds of every element tagged or dimensioned in a view placed on this sheet.
        /// Omitted (not an empty array) when the caller didn't build the index — a live/
        /// incremental edit to just this one sheet reuses the sheet's own existing `elements`
        /// value rather than pay for a whole-document index it doesn't otherwise need; the next
        /// full pass fills it in.</param>
        public static JsonObject BuildSheet(ViewSheet sheet, IReadOnlyDictionary<string, List<string>>? sheetElements = null)
        {
            var fields = new JsonObject
            {
                ["number"] = sheet.SheetNumber,
                ["name"] = sheet.Name,
            };
            try
            {
                var views = new JsonArray();
                foreach (var vid in sheet.GetAllPlacedViews()) views.Add(vid.Value);
                if (views.Count > 0) fields["views"] = views;
            }
            catch { }
            try
            {
                var revIds = sheet.GetAllRevisionIds();
                if (revIds is not null && revIds.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var r in revIds) arr.Add(r.Value);
                    fields["revisions"] = arr;
                }
            }
            catch { }
            if (sheetElements is not null && sheetElements.TryGetValue(sheet.SheetNumber, out var elementUids) && elementUids.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var uid in elementUids) arr.Add(uid);
                fields["elements"] = arr;
            }
            return fields;
        }

        // Bounds sheet.elements/BuildSheetElementIndex — a runaway dimension chain or a sheet
        // with an unusual number of tags must never blow up a single sheet record.
        private const int MaxElementsPerSheet = 500;

        /// <summary>Sheet number → UniqueIds of every element tagged OR dimensioned in a view
        /// placed on that sheet. Reuses <paramref name="taggedSheetsIndex"/> (<see
        /// cref="BuildTaggedSheetIndex"/>'s own element→sheet-numbers map, inverted here) for the
        /// tagged half; dimensions are walked separately (<c>Dimension.References</c> — every
        /// element id a placed-view dimension references), each list capped at
        /// <see cref="MaxElementsPerSheet"/>.</summary>
        public static Dictionary<string, List<string>> BuildSheetElementIndex(
            Document doc, IReadOnlyDictionary<ElementId, List<string>> taggedSheetsIndex)
        {
            var bySheet = new Dictionary<string, List<string>>();
            void Add(string sheetNum, string uid)
            {
                if (!bySheet.TryGetValue(sheetNum, out var list)) bySheet[sheetNum] = list = new List<string>();
                if (list.Count < MaxElementsPerSheet && !list.Contains(uid)) list.Add(uid);
            }

            foreach (var kv in taggedSheetsIndex)
            {
                var el = doc.GetElement(kv.Key);
                if (el is null) continue;
                foreach (var sheetNum in kv.Value) Add(sheetNum, el.UniqueId);
            }

            var viewToSheet = new Dictionary<ElementId, string>();
            foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
            {
                try { foreach (var vid in sheet.GetAllPlacedViews()) viewToSheet[vid] = sheet.SheetNumber; }
                catch { }
            }
            foreach (Dimension dim in new FilteredElementCollector(doc).OfClass(typeof(Dimension)))
            {
                ElementId viewId;
                try { viewId = dim.OwnerViewId; } catch { continue; }
                if (!viewToSheet.TryGetValue(viewId, out var sheetNum)) continue;

                ReferenceArray? refs;
                try { refs = dim.References; } catch { continue; }
                if (refs is null) continue;
                foreach (Reference r in refs)
                {
                    if (r?.ElementId is not { } elId || elId == ElementId.InvalidElementId) continue;
                    var el = doc.GetElement(elId);
                    if (el is not null) Add(sheetNum, el.UniqueId);
                }
            }
            return bySheet;
        }

        /// <param name="sheetsByRevision">Optional (see <see cref="BuildRevisionSheetIndex"/>) —
        /// sheet numbers carrying this revision.</param>
        /// <param name="cloudsByRevision">Optional (see <see cref="BuildRevisionCloudIndex"/>) —
        /// RevisionCloud UniqueIds tagged with this revision.</param>
        public static JsonObject BuildRevision(
            Revision rev,
            IReadOnlyDictionary<ElementId, List<string>>? sheetsByRevision = null,
            IReadOnlyDictionary<ElementId, List<string>>? cloudsByRevision = null)
        {
            var fields = new JsonObject();
            try { fields["sequence"] = rev.SequenceNumber; } catch { }
            try { if (!string.IsNullOrEmpty(rev.RevisionNumber)) fields["number"] = rev.RevisionNumber; } catch { }
            try { fields["date"] = rev.RevisionDate; } catch { }
            try { fields["description"] = rev.Description; } catch { }
            try { fields["issued"] = rev.Issued; } catch { }
            if (sheetsByRevision is not null && sheetsByRevision.TryGetValue(rev.Id, out var sheetNums) && sheetNums.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var s in sheetNums) arr.Add(s);
                fields["sheets"] = arr;
            }
            if (cloudsByRevision is not null && cloudsByRevision.TryGetValue(rev.Id, out var cloudUids) && cloudUids.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var c in cloudUids) arr.Add(c);
                fields["clouds"] = arr;
            }
            return fields;
        }

        /// <summary>Revision ElementId → sheet numbers whose <c>GetAllRevisionIds()</c> includes
        /// it — built once per pass (reverse index), not per revision (which would be an
        /// O(revisions × sheets) walk).</summary>
        public static Dictionary<ElementId, List<string>> BuildRevisionSheetIndex(Document doc)
        {
            var index = new Dictionary<ElementId, List<string>>();
            foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
            {
                IList<ElementId>? revIds;
                try { revIds = sheet.GetAllRevisionIds(); } catch { continue; }
                if (revIds is null) continue;
                foreach (var rid in revIds)
                {
                    if (!index.TryGetValue(rid, out var list)) index[rid] = list = new List<string>();
                    list.Add(sheet.SheetNumber);
                }
            }
            return index;
        }

        /// <summary>Revision ElementId → UniqueIds of every RevisionCloud carrying it — same
        /// once-per-pass reverse-index shape as <see cref="BuildRevisionSheetIndex"/>.</summary>
        public static Dictionary<ElementId, List<string>> BuildRevisionCloudIndex(Document doc)
        {
            var index = new Dictionary<ElementId, List<string>>();
            foreach (RevisionCloud cloud in new FilteredElementCollector(doc).OfClass(typeof(RevisionCloud)))
            {
                ElementId revId;
                try { revId = cloud.RevisionId; } catch { continue; }
                if (revId == ElementId.InvalidElementId) continue;
                if (!index.TryGetValue(revId, out var list)) index[revId] = list = new List<string>();
                list.Add(cloud.UniqueId);
            }
            return index;
        }

        public static JsonObject BuildLink(RevitLinkInstance link, Loam.Revit.Connector.RevitBridge.ModelFacts? linkedFacts)
        {
            var fields = new JsonObject { ["name"] = link.Name };
            try
            {
                var transform = link.GetTotalTransform();
                fields["transform"] = new JsonObject
                {
                    ["origin"] = ToArray(transform.Origin),
                    ["basisX"] = ToArray(transform.BasisX),
                    ["basisY"] = ToArray(transform.BasisY),
                    ["basisZ"] = ToArray(transform.BasisZ),
                };
            }
            catch { }
            if (linkedFacts?.ModelInstanceId is not null) fields["modelInstanceId"] = linkedFacts.ModelInstanceId;
            return fields;
        }

        // ── Change ("chg") ───────────────────────────────────────────────────────

        public static JsonObject BuildChangeHeader(
            IReadOnlyList<string> transactionNames, string? lastChangedBy, int added, int modified, int deleted)
        {
            var fields = new JsonObject
            {
                ["added"] = added,
                ["modified"] = modified,
                ["deleted"] = deleted,
            };
            if (transactionNames.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var n in transactionNames) arr.Add(n);
                fields["transactions"] = arr;
            }
            if (!string.IsNullOrEmpty(lastChangedBy)) fields["by"] = lastChangedBy;
            return fields;
        }

        private static JsonArray ToArray(XYZ p) => new() { p.X, p.Y, p.Z };
    }
}
