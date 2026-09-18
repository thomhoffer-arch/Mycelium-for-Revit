# Revit model-source contract (v0.3)

> **What this is.** The exact interface the Revit connector exposes so Mycelium Studio can drive it. **PDRA** (commercial superset) and this connector both implement it — two implementations, one contract.

## Role boundary (read this first)

A model source **exposes raw Revit data over MCP tools — nothing more.** It does **NOT**:

- construct spine records (identity / freshness / provenance) — **Mycelium Studio** does that from the raw fields;
- run a provenance ledger — **Mycelium Studio** owns it;
- carry any orchestrator/triage/compliance logic.

It only translates Revit ↔ the tool shapes below.

## Transport

- **MCP over Streamable HTTP**, JSON-RPC: `initialize` → `tools/call`.
- Tool output is returned as a **JSON string** in `result.content[0].text`.
- **Auth:** optional bearer (`Authorization: Bearer <token>`). Local-first.
- **Endpoint:** `LOAM_REVIT_LISTEN` (default `http://127.0.0.1:47100/mcp`), token `LOAM_REVIT_TOKEN` — see
  `src/App.cs`. (Previously documented here as `MYCELIUM_REVIT_URL`/`MYCELIUM_REVIT_TOKEN`, which the code
  never read — see the changelog at the bottom of this document.)

## Classification

`list_elements`, `get_element_by_uniqueid`, `get_element_by_ifcguid`, `filter_elements_by_scope_box`,
`get_sheets` (`include_elements: true`), and `get_door_rooms` can all return a `classification` object per
row — assembly/OmniClass codes read off Revit's own built-in fields, by default. An office's real
classification scheme (NL-SfB, Uniclass, …) almost always lives in a differently named shared/project
parameter instead, so on most models the built-ins alone come back empty. Two things close that gap:

- **`classification_params`** (optional arg, every tool above) — extra parameter names to probe, beyond the
  built-ins, read off the type then the instance and merged into `classification{}` under the parameter's
  own name.
- **`get_classification_sources`** — samples the model and reports candidate parameters (name, populated
  count, a few sample values) so a caller can find the right name instead of guessing it. See below.

Every response that can carry `classification` also carries a **`classification_sources`** envelope:

```json
"classification_sources": {
  "supported": true,
  "probed": [
    { "label": "assembly_code",  "parameter": "Assembly Code",    "builtin": "UNIFORMAT_CODE",  "populated": 0 },
    { "label": "omniclass_code", "parameter": "OmniClass Number", "builtin": "OMNICLASS_CODE",  "populated": 0 },
    { "label": "NL-SfB",         "parameter": "NL-SfB",           "builtin": null,              "populated": 1841 }
  ]
}
```

This is how "field unsupported" and "element has no value" stay distinguishable **without** ever writing
`classification: null` onto a row — the connector's identity-bearing fields are always omitted, never
blanked, when absent (see `SpineKeys.cs`; this document's "omit, not blanked" notes below are the same
rule). Read it as: the envelope's presence means the field is supported; a `populated: 0` on every probed
entry means the elements actually returned carry no value for any of them — including the built-ins, which
is the expected, correct result on a model whose office classification lives in its own named parameter and
was never passed via `classification_params`. `populated` counts only the rows the call actually returned
(bounded by `limit`/`sample`), not the whole document.

**Finding the parameter when it isn't type-level.** `get_classification_sources`' default (`scope:
"heuristic"`, unchanged) only matches classification-looking names, and its type-only mode (`scope: "type"`,
same as the legacy `all: true`) never sees instance parameters — an office's classification value is often
set per instance, not per type, and in that case neither mode can find it. Pass `scope: "instance"` (every
instance parameter, no name or storage filter) or `scope: "all"` (both levels, no name or storage filter)
to search name-agnostically. Omit `category` to sample across the whole document — the sample is now spread
across the categories present, not just the first ones in document order.

---

## Tools

> ⚠️ **Wire names are snake_case and exact.** PDRA names the tools `pdra_get_model_revision` etc. internally; the server accepts **both** forms but advertises unprefixed names via `tools/list`.

### `get_model_revision`
Request: `{}`

```json
{
  "version_guid": "string",
  "number_of_saves": 42,
  "has_unsaved_changes": false,
  "title": "string",
  "path": "string",
  "worksharing": "cloud",
  "central_model_path": "string",
  "cloud_project_guid": "string",
  "cloud_model_guid": "string",
  "cloud_region": "string",
  "model_instance_id": "string"
}
```

Freshness stamp. `has_unsaved_changes: true` warns that the cloud copy may not reflect the model.

**Cross-user model identity.** `title` and `path` identify *this user's local copy* of a workshared
model — for a file-based workshared model, two people editing the same central model each report a
different `title` (`SFW_PDR_BWK_R24_thom.hoffer` vs. `SFW_PDR_BWK_R24_jane.doe`) and a different
`path` (each user's own local `Documents` folder). `worksharing` and `central_model_path` /
`cloud_project_guid`+`cloud_model_guid` are the fact that IS identical across every user of a model —
join on those, never on `title`/`path`, when correlating events from more than one user.

- **`worksharing`** — always present, exactly one of:
  | Value | Meaning |
  |---|---|
  | `cloud` | The model lives in the cloud (BIM 360 / ACC, "C4R"). Read from `Document.IsModelInCloud`, so it reports *where the model lives*, **not** that it is workshared — a single-user cloud model reports `cloud` too, and `is_workshared` is what tells those apart. `cloud_project_guid`/`cloud_model_guid` are the identity anchor; `cloud_region` names the account region. |
  | `not_workshared` | A plain, non-workshared local file. No central model exists. |
  | `file_based_central` | This document IS the file-based central model itself (rare — usually only true when opened directly, not detached/local). |
  | `file_based_local` | A local copy of a file-based workshared central model. `central_model_path` names the central. |
  | `file_based_unknown` | Workshared, but the central model path could not be resolved (e.g. a detached model) — never guessed; `central_model_path` stays absent. |
- **`central_model_path`**, **`cloud_project_guid`**, **`cloud_model_guid`**, **`cloud_region`** — present
  only when known (omit, never blank — same rule as everywhere else in this contract); absent for
  `not_workshared` and `file_based_unknown`.
- **`model_instance_id`** — the single combined form of the anchor above: `cloud_project_guid`+
  `cloud_model_guid` when both are known, else `central_model_path`, else absent. This is the
  **document-instance guard** every element-returning tool also stamps on its rows (see "Identity
  rules" below) — use it, not `unique_id`/`ifc_guid` alone, when deciding whether two reads (or two
  users' events) describe the same real element. Absent for `not_workshared`/`file_based_unknown`
  (a genuinely standalone RVT, including a fresh Save As/copy of one, has no cross-copy identity the
  Revit API exposes) — treat absence as "cannot rule out a collision", never as "matches".

---

### `get_project_info`
Request: `{}`

```json
{
  "name":     "string",
  "number":   "string",
  "client":   "string",
  "address":  "string",
  "building": "string",
  "title":    "string",
  "path":     "string"
}
```

- `name` / `number` — from `Document.ProjectInformation`. `name` falls back to `title` when `ProjectInformation.Name` is empty, so a blank-ProjectInformation model still self-identifies.
- `title` — `Document.Title` (always populated; the `.rvt` filename without extension).
- `path` — `Document.PathName` (always populated; full file path or cloud model path).
- `client` / `address` / `building` — optional context from `ProjectInformation`.

Mycelium Studio uses these to auto-seed the project — if absent, it degrades to learning the project from mail.

---

### `get_rooms`
Request: `{}`

```json
{
  "rooms": [
    { "unique_id": "…", "id": 123, "number": "1.01", "name": "Kantoor", "area_sf": 215.3,
      "area_user_units": "20.0 m²",
      "level": { "id": 456, "name": "01 begane grond", "elevation_ft": 0.0, "elevation_user_units": "0.00 m" } }
  ]
}
```

`area_sf` is always square feet (Revit's internal unit); `area_user_units` is the document-display string,
omitted when empty. `level` is the same nested `{id, name, elevation_ft, elevation_user_units}` shape every
other tool uses (see `get_levels` below and `ElementContextReader.ResolveLevel`), omitted when unresolvable
— not the flat `level_name`/`area_sqft`/`area_display` fields a prior version of this document showed (see
the changelog).

---

### `get_levels`
Request: `{}`

```json
{
  "levels": [
    { "unique_id": "…", "id": 123, "name": "01 begane grond", "elevation_ft": 0.0, "elevation_user_units": "0.00 m" }
  ]
}
```

`elevation_ft` is always feet (Revit's internal unit); `elevation_user_units` is the document-display
string. A prior version of this document showed `elevation`/`elevation_display` (see the changelog).

Sorted by elevation ascending.

---

### `get_views`
Request: `{}`

```json
{
  "views": [
    { "unique_id": "…", "id": 123, "name": "Floor Plan: Level 1", "view_type": "FloorPlan", "level_name": "Level 1" }
  ]
}
```

Excludes templates and ViewSheets. Includes floor plans, sections, elevations, 3D views, drafting views, schedules.

---

### `get_sheets`
Request: `{ "include_elements": false }` — with `include_elements: true`, also accepts `classification_params`.

```json
{
  "sheets": [
    { "unique_id": "…", "sheet_number": "A101", "sheet_name": "Floor Plan", "current_revision": "3",
      "views": [ { "unique_id": "…", "name": "…", "view_type": "FloorPlan" } ] }
  ],
  "model_instance_id": "string"
}
```

Each sheet row is `unique_id`/`sheet_number`/`sheet_name` (**not** `name`, and **no** numeric `id` — a
prior version of this document showed both; see the changelog) plus `current_revision` when the
`SHEET_CURRENT_REVISION` built-in parameter is available on this Revit version, and `views`.

`model_instance_id` (top-level, present when resolvable) is the document-instance guard — see
`get_model_revision`'s entry above. `include_elements: true` adds visible element data per view, and **requires `sheet_number`**; each element row also carries `model_instance_id` (same value as the top-level one — this call reads a single host document).
Fetching a view's elements this way makes Revit regenerate that view's graphics if it isn't
already cached (the "Generating graphics for ..." status-bar message); requiring `sheet_number`
keeps that to the handful of views placed on one sheet instead of every view in the document. A
call with `include_elements: true` and no `sheet_number` is rejected with an error pointing at
`list_elements` — use that tool for bulk/model-wide element enumeration (e.g. resyncing after a
large change); it walks the document directly and never touches per-view graphics.

---

### `get_links`
Request: `{}`

```json
{
  "links": [
    { "unique_id": "…", "id": 123, "name": "Structure.rvt", "loaded": true, "title": "Structure.rvt", "project_key": "…" }
  ]
}
```

The loaded flag is `loaded` (**not** `is_loaded` — a prior version of this document showed the latter; see
the changelog). `title` and `project_key` are set only for loaded links and matches the key stamped on
elements from that link.

---

### `list_elements`
Request: `{ "category": "OST_Walls", "view_id": 123, "limit": 200, "offset": 0, "params": ["Height"], "classification_params": ["NL-SfB"] }` — every field optional; omit `category` to enumerate the whole document.

```json
{
  "count": 200,
  "truncated": true,
  "elements": [
    {
      "unique_id": "f382087d-…",
      "id": 1234567,
      "category": "Walls",
      "category_id": "OST_Walls",
      "name": "Basic Wall: Exterior",
      "type_id": 654321,
      "type_name": "Exterior - Brick on CMU",
      "mark": "D-104",
      "from_link": false,
      "design_option": { "id": 111, "name": "Option 1", "is_primary": true },
      "room": { "id": 222, "name": "Kantoor", "number": "1.01", "level_name": "01" },
      "ifc_guid": "0X3$tP9…",
      "model_instance_id": "string",
      "level": { "id": 456, "name": "01 begane grond", "elevation_ft": 0.0, "elevation_user_units": "0.00 m" },
      "classification": { "assembly_code": "22.20", "assembly_description": "…", "NL-SfB": "21.21" },
      "params": { "Height": { "storage_type": "Double", "has_value": true, "value": 10.0, "unit": "ft", "display": "3.05 m" } }
    }
  ],
  "classification_sources": { "supported": true, "probed": [ "…see Classification section above…" ] },
  "offset": 0,
  "next_offset": 200,
  "model_instance_id": "string"
}
```

`model_instance_id` (per-row and top-level, present when resolvable) is the document-instance guard —
see `get_model_revision`'s entry above. The general identity primitive — no scope box, no id, no category all required. `filter_elements_by_scope_box`
needs a scope box; `get_element_by_uniqueid`/`get_element_by_ifcguid` need an id you already have; the typed
getters (`get_rooms`/`get_levels`/`get_views`/`get_sheets`/`get_links`) each cover one narrow category. This
is how a caller with no prior identity discovers what's in the model. Unscoped (`category` omitted) it walks
the whole document, bounded by `limit`, in document order (no natural sort across mixed categories);
scoped, it behaves like `filter_elements_by_scope_box`'s own category resolution. The `category` **request**
arg accepts either the BuiltInCategory enum name (`"OST_Walls"`) or the document's display name (`"Walls"`,
enum name tried first) — so a value read off a row's own `category` or `category_id` **response** field
round-trips into a later call without a Revit-specific name table on the caller's side. `category_id` is
the row's BuiltInCategory enum name, present only for a built-in category (omitted for a custom/family
category with no BuiltInCategory equivalent). `type_id`/`type_name`, `mark` (`ALL_MODEL_MARK`),
`design_option`, `room`, `ifc_guid`, `level`, and `classification` are omitted (not blanked) when the
element carries none — see the Classification section above for `classification_params` /
`classification_sources`. `from_link` is always present (`false` on every row this tool returns — it
never traverses links, unlike `get_element_by_uniqueid`). `room` is geometrically resolved via
`Document.GetRoomAtPoint` against the active view's phase (else the document's last phase) — see the
Identity rules table below for how `room` differs from `get_door_rooms`' door-specific `from_room`/
`to_room`. `params` (request arg) names extra parameters to read per row, returned typed under
`params[name]` (see `get_element_parameters` below for the shape) — never merged into `classification{}`.
`offset` (request arg) pages through a large result with a stable ElementId-ascending ordering: pass it
(`0` for the first page) on every call once paging, and read `next_offset` off a truncated response for
the following page; omitting it entirely uses the original unordered document-order walk, and the two
orderings do not compose.

---

### `filter_elements_by_scope_box`
Request: `{ "scope_box_id": 123, "category": "OST_Doors", "inside_only": true, "params": ["Fire Rating"] }`

```json
{
  "scope_box": { "id": 999, "name": "Zone B" },
  "mode": "centroid",
  "count_in": 12,
  "count_out": 3,
  "elements": [
    {
      "unique_id": "f382087d-…",
      "id": 1234567,
      "name": "M_Single-Flush",
      "category": "Doors",
      "category_id": "OST_Doors",
      "in_box": true,
      "type_id": 654321,
      "type_name": "36\" x 84\"",
      "mark": "D-104",
      "ifc_guid": "0X3$tP9…",
      "model_instance_id": "string",
      "design_option": { "id": 111, "name": "Option 1", "is_primary": true },
      "level": { "id": 456, "name": "05 vijfde verdieping", "elevation_ft": 12.0, "elevation_user_units": "3.66 m" },
      "room": { "id": 222, "name": "Gang", "number": "5.01", "level_name": "05 vijfde verdieping" },
      "classification": { "assembly_code": "22.20", "assembly_description": "…" },
      "params": { "Fire Rating": { "storage_type": "String", "has_value": true, "value": "60 min" } },
      "from_link": false,
      "project": "2233 IKC Poeldijk"
    }
  ],
  "classification_sources": { "supported": true, "probed": [ "…see Classification section above…" ] },
  "model_instance_id": "string"
}
```

- **`unique_id`** — primary identity (stable across sessions).
- **`id`** — numeric Revit ElementId. Required by `get_door_rooms`.
- **`model_instance_id`** (per-row, from the element's OWN document — a linked element's differs from
  the host's; and top-level, the host document's own) — the document-instance guard, present when
  resolvable. See `get_model_revision`'s entry above.
- `ifc_guid`, `design_option`, `room`, `level`, and `classification` are omitted (not blanked) when the
  element carries none — a prior version of this tool set `level`/`design_option` to `null` instead of
  omitting them; that has been fixed to match every other tool's convention. `room` is the same
  geometrically-resolved shape `list_elements` carries (see that entry above).
- `type_id`/`type_name` — the element's own type, when it has a distinct one.
- `mark` — `ALL_MODEL_MARK`, the human-facing tag (e.g. "D-104").
- `params` (request arg) — extra parameter names to read per row, typed under `params[name]` — see
  `get_element_parameters` below for the shape.
- `category_id` (BuiltInCategory enum name) is present alongside `category` (display name) for a built-in
  category — see `list_elements`' entry above for the round-trip this enables on the `category` request arg.
- `from_link: true` — element is from a linked model.

---

### `get_element_by_uniqueid`
Request: `{ "unique_ids": ["…", "…"], "classification_params": ["NL-SfB"] }`

```json
{
  "elements": [
    {
      "unique_id": "…",
      "found": true,
      "id": 1234567,
      "name": "…",
      "category": "Walls",
      "category_id": "OST_Walls",
      "type_id": 654321,
      "type_name": "…",
      "ifc_guid": "…",
      "source": "pdra",
      "sourceLocalId": "…",
      "projectKey": "revit:…",
      "modelInstanceId": "string",
      "level": { "id": 456, "name": "01 begane grond", "elevation_ft": 0.0, "elevation_user_units": "0.00 m" },
      "classification": { "assembly_code": "22.20", "assembly_description": "…", "NL-SfB": "21.21" }
    }
  ],
  "classification_sources": { "supported": true, "probed": [ "…see Classification section above…" ] }
}
```

Resolves across host document and loaded Revit links. `found: false` when not resolvable. A link hit also
carries `from_link: true`, `link_instance_id`, `link_title` — and its `modelInstanceId` (present when
resolvable) is the LINK's own, not the host's: two elements only prove the same real element when their
`unique_id`/`ifc_guid` AND `modelInstanceId` both match (see `get_model_revision`'s entry above — a
Revit UniqueId is unique only within one document instance, never globally).

---

### `get_element_by_ifcguid`
Request: `{ "ifc_guids": ["…"], "classification_params": ["NL-SfB"] }` → the same element shape as
`get_element_by_uniqueid` (keyed on `ifc_guid` instead of `unique_id`; no link-search, since IFC_GUID isn't
searched inside links today, so `modelInstanceId` is always the host document's own), including `level`,
`classification`, and the `classification_sources` envelope.

Fallback identity path — use `unique_id` as primary.

---

### `get_door_rooms`
Request: `{ "element_ids": [1234567, …], "scope_box_id": 123, "limit": 500, "classification_params": ["NL-SfB"] }`

(`element_ids` are the **numeric** ids from `filter_elements_by_scope_box`.)

```json
{
  "count": 1,
  "phase": "New Construction",
  "elements": [
    {
      "unique_id": "…",
      "id": 1234567,
      "ifc_guid": "…",
      "model_instance_id": "string",
      "type_id": 654321,
      "type_name": "…dm09…",
      "mark": "D-104",
      "host_id": 7654321,
      "NLRS_C_breedte_01": "850",
      "classification": { "NL-SfB": "23.21" },
      "from_room": { "id": 111, "name": "…", "number": "1.01", "level_name": "01", "params": { "NLRS_C_ruimtefunctie": "verblijfsruimte" } },
      "to_room":   { "id": 112, "name": "…", "number": "1.02", "level_name": "01", "params": { "NLRS_C_ruimtefunctie": "hal" } },
      "resolution": "from_to_room"
    }
  ],
  "classification_sources": { "supported": true, "probed": [ "…see Classification section above…" ] },
  "model_instance_id": "string"
}
```

Uses Revit From/To Room assignment; falls back to geometric room lookup (`resolution` reports which:
`from_to_room` | `geometric` | `none`). Service doors may return `null` for one room side.

`door_params` values (e.g. `NLRS_C_breedte_01` above) are always **strings** — `ElementContextReader.
ReadParamValue` reads `AsValueString()` first (a formatted display string, e.g. `"850"` or `"850 mm"`
depending on the document's display units), never a bare number — a prior version of this document showed
an unquoted numeric literal (`850`) here, which the tool never actually returns (see the changelog). Use
`get_element_parameters` or `list_elements`'/`filter_elements_by_scope_box`'s `params[]` arg instead of
`door_params` when a numeric, typed value is needed.

---

### `get_classification_sources`
Request: `{ "category": "OST_Walls", "sample": 500, "scope": "instance" }` — every field optional (`category`
accepts the enum name or the document's display name); see the Classification section above.

```json
{
  "sampled": 500,
  "sources": [
    { "parameter": "NL-SfB", "level": "type", "storage": "String", "populated": 468,
      "sample_values": ["21.21", "22.11", "31.21"], "builtin": null },
    { "parameter": "Assembly Code", "level": "type", "storage": "String", "populated": 0,
      "sample_values": [], "builtin": "UNIFORMAT_CODE" }
  ]
}
```

Reports candidates, ordered by `populated` descending — it does **not** decide which one is authoritative
(that judgment stays out of the connector; see Role boundary above). `storage` (`String`, `Integer`,
`ElementId`, …) is always reported as evidence, never used to filter out a candidate except in `scope:
"type"` (below). Pass `parameter_names` to check exact names instead of `scope`'s name-pattern heuristic.

`scope` controls what gets scanned:

| `scope` | Levels scanned | Name filter | Storage filter |
|---|---|---|---|
| `heuristic` (default) | type + instance | `sfb`, `uniclass`, `omniclass`, `uniformat`, `assembly code`, `classification` | none |
| `type` | type only | none | `String` only |
| `instance` | instance only | none | none |
| `all` | type + instance | none | none |

`scope: "type"` is the same scan as the legacy `all: true` (still accepted, unchanged, for existing
callers — `scope` takes priority when both are passed). Neither `heuristic` nor `type` can see an instance
-level parameter with a non-classification-looking name; use `scope: "instance"` or `scope: "all"` for a
model whose classification value is set per instance rather than per type.

Unscoped (`category` omitted), the sample is spread across the categories present in the document rather
than taken in raw document order, so a small `sample` still covers categories beyond whichever ones happen
to sort first.

---

### `get_element_parameters`
Request: `{ "unique_id": "…", "type_params": false }` — identify the element by `unique_id` (preferred) or
`id` (numeric ElementId).

```json
{
  "unique_id": "…",
  "id": 1234567,
  "count": 42,
  "params": [
    { "name": "Comments", "storage_type": "String", "has_value": true, "value": "…" },
    { "name": "Height", "storage_type": "Double", "has_value": true, "value": 10.0, "unit": "ft", "display": "3.05 m" },
    { "name": "Mark", "storage_type": "String", "has_value": false, "value": null }
  ]
}
```

The discovery-first surface: every parameter on ONE element, typed (`storage_type`/`has_value`/`value`/
`unit`/`display`) instead of a bare display string — use this to find out what a given element actually
carries before naming a parameter in `list_elements`'/`filter_elements_by_scope_box`'s `params[]` arg or
`classification_params` elsewhere. `value` is `storage_type`-native (a real number for `Double`/`Integer`,
the raw numeric ElementId for `ElementId`, never a formatted string for those); `unit` (Double storage
only, present only when recognised) is Revit's own INTERNAL unit — feet for length, radians for angle, …
— not a display unit, so a caller converts deterministically instead of parsing `display`'s
locale-formatted text. Pass `type_params: true` to also include the element's TYPE's parameters, each
row flagged `is_type: true`.

---

## Identity rules

| Key | Role |
|---|---|
| `unique_id` | **Primary** join key — stable across sessions. |
| `id` (numeric) | Volatile, but **required** by `get_door_rooms`. |
| `ifc_guid` | Fallback join key. |
| `model_instance_id` / `modelInstanceId` | Document-instance **guard**, not itself a join key — present when resolvable (absent for a standalone, non-workshared, non-cloud document). A Revit UniqueId / IFC GlobalId is unique only WITHIN one document instance, never globally: copying, Save-As-ing, or splitting an RVT can produce two genuinely different documents that share thousands of identical `unique_id`/`ifc_guid` values (Autodesk's own docs concede this for whole-file clones; the Revit-IFC team documents the IFC-export case directly — [autodesk/revit-ifc#378](https://github.com/Autodesk/revit-ifc/issues/378), no built-in "reset document GUIDs"). **Two elements only prove the same real-world thing when their join key (`unique_id` or `ifc_guid`) matches AND `model_instance_id` also matches** — never on the join key alone, and never treat two `null`/absent `model_instance_id`s as matching each other. Snake_case (`model_instance_id`) on model-level/bulk-envelope responses (`get_model_revision`, `get_sheets`, `list_elements`, `filter_elements_by_scope_box`, `get_door_rooms`); camelCase (`modelInstanceId`) alongside the other spine MUST-keys (`source`/`sourceLocalId`/`projectKey`) on `get_element_by_uniqueid`/`get_element_by_ifcguid`. A linked element carries the **link's own** identity, not the host's. |
| `category_id` | BuiltInCategory enum name for a row's category, when built-in — feed it back as a `category` request arg on any tool that accepts one, alongside or instead of the `category` display name. |

## Scope (today)

All tools are **read-only**. Write-back (`edit_element` / `create_workitem`) is not implemented — when added it will be additive and gated.

## Changelog

- **v0.3 (Workstream B — additive):**
  - `list_elements` gains `type_id`/`type_name`, `mark`, `design_option`, `from_link` (always `false` —
    this tool never traverses links), `room` (geometric, via the new `ElementContextReader.ResolveRoom`),
    a `params[]` request arg (typed, own top-level `params{}` key — never merged into `classification{}`),
    and `offset`/`next_offset` paging with a stable ElementId-ascending ordering.
  - `filter_elements_by_scope_box` gains `type_id`/`type_name`, `mark`, `room`, and the same `params[]` arg.
  - `get_door_rooms` gains `type_id` (alongside its existing `type_name`).
  - New tool **`get_element_parameters`**: every parameter on one element, typed
    (`storage_type`/`has_value`/`value`/`unit`/`display`) — the discovery-first surface this document's
    consumer (Loam) already expected to exist.
  - The Revit document-changed push (`POST /api/model-event`, not an MCP tool — see `README.md`) gains
    `addedElementIds`/`modifiedElementIds` (split, additive alongside the existing merged
    `changedElementIds`), `deletedIds` (numeric, no UniqueId survives a deletion), `transactionNames`, and
    `lastChangedBy` (workshared models only).
- **Doc reconciliation (no code change — this document was wrong, not the connector):** an external
  analysis (Loam) found this document had drifted from the actual code in six places. All fixed above:
  - `get_rooms` documented flat `level_name`/`area_sqft`/`area_display`; the code has always returned a
    nested `level{}` object plus `area_sf`/`area_user_units`.
  - `get_levels` documented `elevation`/`elevation_display`; the code has always returned
    `elevation_ft`/`elevation_user_units`.
  - `get_links` documented `is_loaded`; the code has always returned `loaded` (and `name`, not the
    document's own `Title`, for the unloaded case — `title` is separate and loaded-only).
  - `get_sheets` documented a numeric `id` field and a `name` field on each sheet row; the code has never
    emitted either — the real fields are `unique_id`/`sheet_number`/`sheet_name`.
  - `get_door_rooms` documented `door_params` values (e.g. `NLRS_C_breedte_01`) as an unquoted number;
    `ElementContextReader.ReadParamValue` has always returned a formatted **string**.
  - `README.md`'s Transport table and this document's own Transport section documented
    `MYCELIUM_REVIT_LISTEN`/`MYCELIUM_REVIT_URL`/`MYCELIUM_REVIT_TOKEN`; `src/App.cs` has always read
    `LOAM_REVIT_LISTEN`/`LOAM_REVIT_TOKEN` — setting the documented vars silently left bearer auth off.
  See `test/` for this repo's precedent on guarding doc/implementation drift (e.g. the `unique_id`/
  `ifc_guid` document-instance-guard fix in `ROADMAP.md`'s "Fixed" section) — a follow-up guard test
  comparing this document's field names against each tool's actual output would close this class of
  drift the way `orchestrator/test/doc_tool_count.test.js` does in the sibling Loam repo, but that harness
  does not exist in this C#/Revit repo today (no test project — see `ROADMAP.md`'s own note that the
  connector's only verification today is the manual `tools/selftest.ps1`, which already checks field
  names/shapes against this document and would have caught this drift had it been run against a model
  exercising these fields).
