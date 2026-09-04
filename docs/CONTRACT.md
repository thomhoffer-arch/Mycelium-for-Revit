# Revit model-source contract (v0.2)

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
- **Endpoint:** `MYCELIUM_REVIT_URL` (default `http://127.0.0.1:47100/mcp`), token `MYCELIUM_REVIT_TOKEN`.

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
  "cloud_region": "string"
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
    { "unique_id": "…", "id": 123, "number": "1.01", "name": "Kantoor", "level_name": "01", "area_sqft": 215.3, "area_display": "20.0 m²" }
  ]
}
```

---

### `get_levels`
Request: `{}`

```json
{
  "levels": [
    { "unique_id": "…", "id": 123, "name": "01 begane grond", "elevation": 0.0, "elevation_display": "0.00 m" }
  ]
}
```

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
    { "unique_id": "…", "id": 123, "sheet_number": "A101", "name": "Floor Plan", "views": [ { "unique_id": "…", "name": "…" } ] }
  ]
}
```

`include_elements: true` adds visible element data per view, and **requires `sheet_number`**.
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
    { "unique_id": "…", "name": "Structure.rvt", "is_loaded": true, "project_key": "…" }
  ]
}
```

`project_key` is set only for loaded links and matches the key stamped on elements from that link.

---

### `list_elements`
Request: `{ "category": "OST_Walls", "view_id": 123, "limit": 200, "classification_params": ["NL-SfB"] }` — every field optional; omit `category` to enumerate the whole document.

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
      "ifc_guid": "0X3$tP9…",
      "level": { "id": 456, "name": "01 begane grond", "elevation_ft": 0.0, "elevation_user_units": "0.00 m" },
      "classification": { "assembly_code": "22.20", "assembly_description": "…", "NL-SfB": "21.21" }
    }
  ],
  "classification_sources": { "supported": true, "probed": [ "…see Classification section above…" ] }
}
```

The general identity primitive — no scope box, no id, no category all required. `filter_elements_by_scope_box`
needs a scope box; `get_element_by_uniqueid`/`get_element_by_ifcguid` need an id you already have; the typed
getters (`get_rooms`/`get_levels`/`get_views`/`get_sheets`/`get_links`) each cover one narrow category. This
is how a caller with no prior identity discovers what's in the model. Unscoped (`category` omitted) it walks
the whole document, bounded by `limit`, in document order (no natural sort across mixed categories);
scoped, it behaves like `filter_elements_by_scope_box`'s own category resolution. The `category` **request**
arg accepts either the BuiltInCategory enum name (`"OST_Walls"`) or the document's display name (`"Walls"`,
enum name tried first) — so a value read off a row's own `category` or `category_id` **response** field
round-trips into a later call without a Revit-specific name table on the caller's side. `category_id` is
the row's BuiltInCategory enum name, present only for a built-in category (omitted for a custom/family
category with no BuiltInCategory equivalent). `ifc_guid`, `level`, and `classification` are omitted (not
blanked) when the element carries none — see the Classification section above for `classification_params`
/ `classification_sources`.

---

### `filter_elements_by_scope_box`
Request: `{ "scope_box_id": 123, "category": "OST_Doors", "inside_only": true }`

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
      "ifc_guid": "0X3$tP9…",
      "design_option": { "id": 111, "name": "Option 1", "is_primary": true },
      "level": { "id": 456, "name": "05 vijfde verdieping", "elevation_ft": 12.0, "elevation_user_units": "3.66 m" },
      "classification": { "assembly_code": "22.20", "assembly_description": "…" },
      "from_link": false,
      "project": "2233 IKC Poeldijk"
    }
  ],
  "classification_sources": { "supported": true, "probed": [ "…see Classification section above…" ] }
}
```

- **`unique_id`** — primary identity (stable across sessions).
- **`id`** — numeric Revit ElementId. Required by `get_door_rooms`.
- `ifc_guid`, `design_option`, `level`, and `classification` are omitted (not blanked) when the element
  carries none — a prior version of this tool set `level`/`design_option` to `null` instead of omitting
  them; that has been fixed to match every other tool's convention.
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
      "level": { "id": 456, "name": "01 begane grond", "elevation_ft": 0.0, "elevation_user_units": "0.00 m" },
      "classification": { "assembly_code": "22.20", "assembly_description": "…", "NL-SfB": "21.21" }
    }
  ],
  "classification_sources": { "supported": true, "probed": [ "…see Classification section above…" ] }
}
```

Resolves across host document and loaded Revit links. `found: false` when not resolvable. A link hit also
carries `from_link: true`, `link_instance_id`, `link_title`.

---

### `get_element_by_ifcguid`
Request: `{ "ifc_guids": ["…"], "classification_params": ["NL-SfB"] }` → the same element shape as
`get_element_by_uniqueid` (keyed on `ifc_guid` instead of `unique_id`; no link-search, since IFC_GUID isn't
searched inside links today), including `level`, `classification`, and the `classification_sources` envelope.

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
      "type_name": "…dm09…",
      "NLRS_C_breedte_01": 850,
      "classification": { "NL-SfB": "23.21" },
      "from_room": { "id": 111, "name": "…", "number": "1.01", "level_name": "01", "params": { "NLRS_C_ruimtefunctie": "verblijfsruimte" } },
      "to_room":   { "id": 112, "name": "…", "number": "1.02", "level_name": "01", "params": { "NLRS_C_ruimtefunctie": "hal" } },
      "resolution": "from_to_room"
    }
  ],
  "classification_sources": { "supported": true, "probed": [ "…see Classification section above…" ] }
}
```

Uses Revit From/To Room assignment; falls back to geometric room lookup (`resolution` reports which:
`from_to_room` | `geometric` | `none`). Service doors may return `null` for one room side.

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

## Identity rules

| Key | Role |
|---|---|
| `unique_id` | **Primary** join key — stable across sessions. |
| `id` (numeric) | Volatile, but **required** by `get_door_rooms`. |
| `ifc_guid` | Fallback join key. |
| `category_id` | BuiltInCategory enum name for a row's category, when built-in — feed it back as a `category` request arg on any tool that accepts one, alongside or instead of the `category` display name. |

## Scope (today)

All tools are **read-only**. Write-back (`edit_element` / `create_workitem`) is not implemented — when added it will be additive and gated.
