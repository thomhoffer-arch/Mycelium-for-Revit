# Revit model-source contract (v0.1)

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

## Tools

> ⚠️ **Wire names are snake_case and exact.** PDRA names the tools `pdra_get_model_revision` etc. internally; the server accepts **both** forms but advertises unprefixed names via `tools/list`.

### `get_model_revision`
Request: `{}`

```json
{ "version_guid": "string", "number_of_saves": 42, "has_unsaved_changes": false, "title": "string", "path": "string" }
```

Freshness stamp. `has_unsaved_changes: true` warns that the cloud copy may not reflect the model.

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
Request: `{ "include_elements": false }`

```json
{
  "sheets": [
    { "unique_id": "…", "id": 123, "sheet_number": "A101", "name": "Floor Plan", "views": [ { "unique_id": "…", "name": "…" } ] }
  ]
}
```

`include_elements: true` adds visible element data per view. Fetching a view's elements this way
makes Revit regenerate that view's graphics if it isn't already cached (the "Generating graphics
for ..." status-bar message); `view_limit` (default 20, max 200) bounds how many views a single
call does this to, so a broad sweep across many sheets can't surface as an uninterrupted burst
across the whole document. Views beyond the cap are still listed (without elements, `elements_skipped:
true`) and the response sets `views_truncated: true` — page through the rest via `sheet_number`.

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
Request: `{ "category": "OST_Walls", "view_id": 123, "limit": 200 }` — every field optional; omit `category` to enumerate the whole document.

```json
{
  "count": 200,
  "truncated": true,
  "elements": [
    {
      "unique_id": "f382087d-…",
      "id": 1234567,
      "category": "OST_Walls",
      "name": "Basic Wall: Exterior",
      "ifc_guid": "0X3$tP9…",
      "level": { "id": 456, "name": "01 begane grond", "elevation_ft": 0.0, "elevation_user_units": "0.00 m" },
      "classification": { "assembly_code": "22.20", "assembly_description": "…" }
    }
  ]
}
```

The general identity primitive — no scope box, no id, no category all required. `filter_elements_by_scope_box`
needs a scope box; `get_element_by_uniqueid`/`get_element_by_ifcguid` need an id you already have; the typed
getters (`get_rooms`/`get_levels`/`get_views`/`get_sheets`/`get_links`) each cover one narrow category. This
is how a caller with no prior identity discovers what's in the model. Unscoped (`category` omitted) it walks
the whole document, bounded by `limit`, in document order (no natural sort across mixed categories);
scoped, it behaves like `filter_elements_by_scope_box`'s own category resolution. `ifc_guid`, `level`, and
`classification` are omitted (not blanked) when the element carries none.

---

### `filter_elements_by_scope_box`
Request: `{ "scope_box_id": 123, "category": "OST_Doors", "inside_only": true }`

```json
{
  "count_in": 12,
  "elements": [
    {
      "unique_id": "f382087d-…",
      "id": 1234567,
      "ifc_guid": "0X3$tP9…",
      "category": "OST_Doors",
      "in_box": true,
      "level_name": "05 vijfde verdieping",
      "design_option_name": null,
      "design_option_is_primary": true,
      "from_link": false
    }
  ]
}
```

- **`unique_id`** — primary identity (stable across sessions).
- **`id`** — numeric Revit ElementId. Required by `get_door_rooms`.
- `from_link: true` — element is from a linked model.

---

### `get_element_by_uniqueid`
Request: `{ "unique_ids": ["…", "…"] }`

```json
{
  "elements": [
    {
      "unique_id": "…",
      "found": true,
      "ifc_guid": "…",
      "name": "…",
      "type_name": "…",
      "level_name": "…",
      "classification": { "assembly_code": "22.20", "assembly_description": "…" }
    }
  ]
}
```

Resolves across host document and loaded Revit links. `found: false` when not resolvable.

---

### `get_element_by_ifcguid`
Request: `{ "ifc_guids": ["…"] }` → same element shape, keyed on `ifc_guid`.

Fallback identity path — use `unique_id` as primary.

---

### `get_door_rooms`
Request: `{ "element_ids": [1234567, …], "scope_box_id": 123, "limit": 500 }`

(`element_ids` are the **numeric** ids from `filter_elements_by_scope_box`.)

```json
{
  "doors": [
    {
      "unique_id": "…",
      "id": 1234567,
      "ifc_guid": "…",
      "type_name": "…dm09…",
      "NLRS_C_breedte_01": 850,
      "from_room": { "function": "verblijfsruimte", "name": "…" },
      "to_room":   { "function": "hal", "name": "…" }
    }
  ]
}
```

Uses Revit From/To Room assignment; falls back to geometric room lookup. Service doors may return only one room side (null on the other).

---

## Identity rules

| Key | Role |
|---|---|
| `unique_id` | **Primary** join key — stable across sessions. |
| `id` (numeric) | Volatile, but **required** by `get_door_rooms`. |
| `ifc_guid` | Fallback join key. |

## Scope (today)

All tools are **read-only**. Write-back (`edit_element` / `create_workitem`) is not implemented — when added it will be additive and gated.
