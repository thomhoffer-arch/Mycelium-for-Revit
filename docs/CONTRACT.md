# Revit model-source contract (v0.1)

> **What this is.** The exact interface a Revit model source must expose so the Loam orchestrator
> can drive it. **PDRA** (commercial superset) and **`loam-revit-connector`** (free first-party open
> connector) both implement it — two implementations, one contract. The orchestrator binds *this*,
> never a vendor (`src/sources/model_source.js`, selected by `LOAM_MODEL_SOURCE`).

## Role boundary (read this first)

A model source **exposes raw Revit data over MCP tools — nothing more.** It does **NOT**:

- construct spine records (identity / freshness / provenance) — **Loam** does that from the raw fields;
- run a provenance ledger — **Loam** owns it;
- carry any orchestrator/triage/compliance logic.

It only translates Revit ↔ the tool shapes below. (Spine *contract* terms are canonical in
[Mycelium](https://github.com/thomhoffer-arch/Mycelium); this doc is the Revit **source** contract
the connector implements.)

## Transport

- **MCP over Streamable HTTP**, JSON-RPC: `initialize` → `tools/call`.
- Tool output is returned as a **JSON string** in `result.content[0].text` (Loam `JSON.parse`s it).
- **Auth:** optional bearer (`Authorization: Bearer <token>`). Local-first.
- **Endpoint Loam dials:** `LOAM_REVIT_URL` (default `http://127.0.0.1:47100/mcp`),
  token `LOAM_REVIT_TOKEN`. Set `LOAM_MODEL_SOURCE=revit-connector` to select this backend.

## The five tools

> ⚠️ **Wire names are snake_case and exact.** These are the MCP `tools/call` names Loam sends.
> (`getModelRevision` etc. are Loam's *internal* method names — never on the wire.) Note the
> **singular** `element` in the two get-by-id tools.

### 1. `get_model_revision`
Request: `{}`
```json
{ "version_guid": "string", "number_of_saves": 42, "has_unsaved_changes": false }
```
Used for the freshness stamp + the project pulse. `has_unsaved_changes: true` warns that CC may not
yet reflect the model.

### 2. `filter_elements_by_scope_box`
Request: `{ "scope_box_id": 123, "category": "OST_Doors", "inside_only": true }`
(Loam calls this once per category in the profile's `scopeBox.categories`. `category` is a **string**,
not an array.)
```json
{
  "count_in": 12,
  "elements": [
    {
      "unique_id": "f382087d-…-0002ee7f2",
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
Field semantics Loam reads:
- **`unique_id`** — Revit UniqueId, the **primary identity** (stable across sessions).
- **`id`** — numeric Revit ElementId. **Required** — `get_door_rooms` keys on it.
- `ifc_guid` — optional, fallback identity.
- `in_box` — Loam treats `in_box !== false` as inside (with `inside_only:true` you can omit it).
- `level_name` (or `level: { "name": … }`) — for the architectural-levels-only filter.
- `design_option_*` — for the accepted/primary-design-option filter (omit/null if not in an option set).
- `from_link` — `true` for elements from a linked model; **Loam drops these**.

Loam also accepts `elements` under `results`, a bare array, or a top-level id array
(`element_ids`/`ids`/`in_box_ids`/`inside_ids`) — but the shape above is preferred.

### 3. `get_element_by_uniqueid`
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
- `found: false` (or omit the element) when not resolvable.
- **Classification is the finance join.** Provide the code under `classification.assembly_code`
  (or top-level `assembly_code`, or `omniclass`). For the `nl` profile it must match the NL-SfB
  shape `^\d{1,2}(\.\d{1,3})?$` to join POs tagged in that system.

### 4. `get_element_by_ifcguid`
Request: `{ "ifc_guids": ["…"] }` → same element shape, keyed on `ifc_guid`.
- ⚠️ Loam keeps only elements with `found: true` here (stricter than #3, where it keeps
  `found !== false`). This is the **fallback** path; `unique_id` is primary.

### 5. `get_door_rooms`
Request: `{ "element_ids": [1234567, …], "scope_box_id": 123, "limit": 500 }`
(`element_ids` are the **numeric** ids from #2.)
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
Field semantics Loam reads (door clear-width rule, Bbl-4.180):
- **clear width** — under one of `NLRS_C_breedte_01` / `breedte_01` / `clear_width` / `width`
  (profile `door.clearWidthParam`). Millimetres; a value `< 10` is treated as metres (×1000).
- **`type_name`** — must carry the width token `dm##` (e.g. `…dm09…` → 850–900 mm range) and the
  **service token** (`_mk` / `meterkast`) for service doors (profile `door.widthTokenRegex` /
  `serviceTokenRegex`).
- **rooms** — either `rooms: [ … ]` or `from_room` / `to_room` (each a room object or **null** —
  a service door may return only the corridor side). Each room's **function label** lives under any
  of `function` / `ruimtefunctie` / `gebruiksfunctie` / `name` (profile `door.roomFunctionParams`);
  Loam maps it to habitable / sanitary / tech / … via the profile's room tokens.
- Loam also accepts `doors` under `results` or a bare array.

## Identity rules

| Key | Role |
|---|---|
| `unique_id` | **Primary** join key — stable across sessions. |
| `id` (numeric) | Volatile, but **required** by `get_door_rooms`. |
| `ifc_guid` | Fallback join key. |

## Profile coupling

The exact field names above (`NLRS_C_breedte_01`, the `dm##` token, room-function keys, the NL-SfB
classification shape) come from the **active profile** (`src/profiles/nl.json`). A connector for a
different firm/standard emits values under **that** profile's expected names — the engine stays
generic; the profile + this contract are what a connector targets.

## Scope (today)

This is the **read** surface — and it is the *complete* set the orchestrator calls today
(model revision, scope-box membership, element-by-uniqueId, element-by-ifcGuid, door→rooms).
Implement these five correctly and Loam's Revit-dependent features work end-to-end: freshness,
zone resolution, classification/finance enrichment, door compliance, deleted-vs-fixed.

Revit **write-back** (`edit_element` / `create_workitem`) is declared in Loam's propose→approve
layer but is **not executed against a Revit source today** (writes are propose-only), so no write
tools are required yet. When write-back is built, this contract gains gated, reversible,
ledger-emitting write primitives — additively (contract semver: additive → minor).

## Conformance (how Loam exercises it)

1. `get_model_revision` → freshness stamp.
2. `filter_elements_by_scope_box` per profile category → in-box uniqueIds + numeric ids.
3. `get_element_by_uniqueid` / `get_element_by_ifcguid` → enrich each clash side (classification join).
4. `get_door_rooms` over the in-box door ids → relational Bbl-4.180 verdicts.

A door serving a prescribed space must return its rooms' functions **and** its clear width for the
rule to produce `pass`/`fail` rather than `needs_review`.
