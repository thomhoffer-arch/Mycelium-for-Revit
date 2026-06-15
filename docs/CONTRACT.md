# Loam model-source contract — field reference

Authoritative shapes the connector emits. Loam consumes these to build spine records; field name drift breaks ingestion silently.

## `get_model_revision`

Request: `{}`

```json
{ "version_guid": "string", "number_of_saves": 42, "has_unsaved_changes": false }
```

## `filter_elements_by_scope_box`

Request: `{ "scope_box_id": 123, "category": "OST_Doors", "inside_only": true }`

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
      "design_option_is_primary": null,
      "from_link": false
    }
  ]
}
```

- Loam treats `in_box !== false` as inside.
- `from_link: true` is dropped by Loam.
- `level_name` may also be emitted as `level: { name: "..." }`.

## `get_element_by_uniqueid`

Request: `{ "unique_ids": ["…","…"] }`

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

- Misses: emit `{ "unique_id": "…", "found": false }`, OR omit the row.
- Classification: nest under `classification.assembly_code`, OR top-level `assembly_code`, OR `omniclass`.
- `nl` profile requires NL-SfB shape `^\d{1,2}(\.\d{1,3})?$` — values that don't match are nulled.

## `get_element_by_ifcguid`

Request: `{ "ifc_guids": ["…"] }` → same element shape, keyed on `ifc_guid`.

⚠️ Loam keeps only `found: true` rows here (stricter than `get_element_by_uniqueid`).

## `get_door_rooms`

Request: `{ "element_ids": [1234567,…], "scope_box_id": 123, "limit": 500 }`

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

- `type_name` carries width tokens (`dm##`) AND service tokens (`_mk`, `meterkast`).
- Width is in mm. Loam multiplies by 1000 if value `< 10` (interprets as metres).
- Room function key resolution order (any of these — Bbl-4.180 keys off the first match): `function`, `ruimtefunctie`, `gebruiksfunctie`, `name`.
- Rooms may also be emitted as `"rooms": [ { function, name }, … ]` instead of from/to.

## Identity rules

| key | role | stability |
|---|---|---|
| `unique_id` | primary | stable across saves |
| `id` | required by `get_door_rooms` | volatile (per-session ElementId) |
| `ifc_guid` | fallback | stable when present |

## Profile

Field names like `NLRS_C_breedte_01`, the `dm##` token convention, and room-function keys come from the active profile (`src/Profiles/nl.json`). A different firm = a different profile JSON.
