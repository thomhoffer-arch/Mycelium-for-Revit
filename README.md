# loam-revit-connector

A **Revit model source** for the [Loam](https://github.com/thomhoffer-arch/Loam) orchestrator. Exposes Revit data over **MCP** so Loam can build spine records — the connector itself does **not** emit spine records and does **not** maintain a provenance ledger.

Authoritative wire contract: [REVIT_MODEL_SOURCE_CONTRACT.md](https://github.com/thomhoffer-arch/loam/blob/main/docs/connectors/REVIT_MODEL_SOURCE_CONTRACT.md) (Loam) — `docs/CONTRACT.md` in this repo is a local mirror.

---

## Contract

Transport: **MCP over Streamable HTTP**, JSON-RPC (`initialize` → `tools/call`). Tool output is returned as a JSON string in `result.content[0].text`. Optional bearer auth via `Authorization: Bearer <token>`.

Defaults:

| env var | default |
|---|---|
| `LOAM_REVIT_LISTEN` | `http://127.0.0.1:47100/mcp/` |
| `LOAM_REVIT_TOKEN`  | _unset = no auth_ |

Tools — names are **snake_case wire names** and shapes are part of the contract; do not rename without coordinating with Loam:

1. `get_model_revision` → `{ version_guid, number_of_saves, has_unsaved_changes }`
2. `filter_elements_by_scope_box(scope_box_id, category, inside_only)` → `{ count_in, elements: [...] }`
3. `get_element_by_uniqueid(unique_ids[])` → `{ elements: [...] }` (misses returned as `found: false`)
4. `get_element_by_ifcguid(ifc_guids[])` → `{ elements: [...] }` (only `found: true` rows — stricter than #3)
5. `get_door_rooms(element_ids[], scope_box_id?, limit?)` → `{ doors: [...] }`

Full field-level reference: `docs/CONTRACT.md`.

### Identity

- `unique_id` — **primary**, stable across saves (Revit `UniqueId`).
- `id` — numeric `ElementId`, volatile but **required** by `get_door_rooms`.
- `ifc_guid` — fallback identity (parameter `IfcGUID`).

### Profiles

Firm-specific parameter names (door clear width, room function, classification code) live in `src/Profiles/*.json`. The default is `nl.json` (NL-SfB, NLRS). Swap profiles per firm; the tool field names follow the profile.

---

## Build & install

Prereqs: Visual Studio 2022, Revit 2024 SDK on disk (default `C:\Program Files\Autodesk\Revit 2024`).

```
dotnet build LoamRevitConnector.sln -c Release
```

Copy `LoamRevitConnector.dll`, `Newtonsoft.Json.dll`, `Profiles\nl.json`, and `LoamRevitConnector.addin` to:

```
%APPDATA%\Autodesk\Revit\Addins\2024\
```

Launch Revit; the MCP server starts on port 47100. Point Loam at it:

```
LOAM_REVIT_URL=http://127.0.0.1:47100/mcp
```

---

## Repo layout

```
src/
  App.cs                      # IExternalApplication entry — boots MCP server
  Mcp/
    McpServer.cs              # HttpListener + JSON-RPC dispatcher
    JsonRpc.cs                # request/response/tool-result types
    Tools/
      GetModelRevision.cs
      FilterElementsByScopeBox.cs
      GetElementByUniqueId.cs
      GetElementByIfcGuid.cs
      GetDoorRooms.cs
  RevitBridge/
    RevitContext.cs           # ExternalEvent marshalling to UI thread
    ElementMapper.cs          # Revit Element -> Loam DTO
  Profiles/
    Profile.cs
    nl.json                   # NL-SfB / NLRS defaults
  LoamRevitConnector.addin
  LoamRevitConnector.csproj
docs/
  CONTRACT.md                 # field-level Loam contract reference (mirror)
```

---

## See also

- [Loam (orchestrator)](https://github.com/thomhoffer-arch/Loam)
- [Mycelium (Connective Spine)](https://github.com/thomhoffer-arch/Mycelium)
- [PDRA (Revit MCP tools — reference)](https://github.com/thomhoffer-arch/PDRA)
- [Full contract spec (REVIT_MODEL_SOURCE_CONTRACT.md)](https://github.com/thomhoffer-arch/loam/blob/main/docs/connectors/REVIT_MODEL_SOURCE_CONTRACT.md)
