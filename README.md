# Mycelium Studio — Revit Connector

A **Revit MCP connector** for [Mycelium Studio](https://github.com/thomhoffer-arch/Mycelium). Loads as a Revit add-in, starts an MCP-over-HTTP server on port 47100, and exposes Revit model data as tools that Mycelium Studio (or any MCP client) can call. The connector is **read-only** — it surfaces raw Revit data; Mycelium Studio builds spine records and runs all orchestration logic from that data.

The Revit tool implementations are **PDRA's verbatim** (vendored under `src/Pdra/`). One contract, two front-ends (PDRA and this connector).

---

## What it exposes

All tools are read-only. No writes, no transactions, no side effects.

| Tool | What it returns |
|---|---|
| `get_model_revision` | Freshness stamp: `version_guid`, `number_of_saves`, `has_unsaved_changes`, document title and path |
| `get_project_info` | Project identity from `Document.ProjectInformation`: name, number, client, address, building |
| `list_elements` | The general identity primitive — no scope box, no id, no category required. Omit `category` to walk the whole document (bounded by `limit`); each row carries `unique_id`, `ifc_guid`, `category` (display name), `category_id` (BuiltInCategory enum name), level, classification |
| `get_rooms` | All rooms with number, name, level, area (ft² and display units), `unique_id` |
| `get_levels` | All levels sorted by elevation — `unique_id`, `id`, name, elevation in internal and display units |
| `get_views` | All non-sheet views (plans, sections, elevations, 3D, drafting, schedules) excluding templates |
| `get_sheets` | All drawing sheets with placed views; optionally includes visible element data (incl. classification) per view |
| `get_links` | All Revit links — name, loaded status, `project_key` for loaded links |
| `filter_elements_by_scope_box` | Elements inside (or intersecting) a scope box, by category — with `unique_id`, numeric `id`, `category`/`category_id`, `ifc_guid`, level, design option, classification, link flag |
| `get_element_by_uniqueid` | Resolves one or more UniqueIds (host + loaded links) → name, type, level, classification |
| `get_element_by_ifcguid` | Finds elements by IFC GlobalId — fallback identity path; same element shape as `get_element_by_uniqueid` |
| `get_door_rooms` | Rooms on both sides of each door (Revit From/To Room or geometric fallback) with clear-width parameter, classification, and room function |
| `get_classification_sources` | Samples the model and reports candidate classification parameters (name, level, storage type, populated count, sample values) — for finding an office's NL-SfB/Uniclass parameter name. `scope: "instance"` / `"all"` scan name- and storage-agnostically, including instance parameters, for when the value isn't set at type level |

Every element-returning tool above accepts `classification_params` (extra parameter names to read, beyond the built-in Assembly Code / OmniClass fields) and returns a `classification_sources` envelope reporting what was probed and how many rows had it populated — see `docs/CONTRACT.md`'s Classification section.

PDRA tool names (`pdra_get_model_revision` etc.) are also accepted; the server advertises the unprefixed names via `tools/list`.

---

## Transport

**MCP over Streamable HTTP**, JSON-RPC: `initialize` → `tools/call`. Tool output is a JSON string in `result.content[0].text`. Optional bearer auth via `Authorization: Bearer <token>`.

| env var | default |
|---|---|
| `MYCELIUM_REVIT_LISTEN` | `http://127.0.0.1:47100/mcp` |
| `MYCELIUM_REVIT_TOKEN`  | _unset = no auth_ |

If multiple Revit instances are open, only the first one serves MCP requests — subsequent instances load silently (port already owned).

---

## Event push (additive)

Besides answering MCP calls, the connector **pushes** Revit document events to the orchestrator so it doesn't have to poll. On open / save / sync / change / close it fires a fire-and-forget `POST` to:

```
POST http://127.0.0.1:47600/api/model-event      (override port with LOAM_HTTP_PORT)
{ "kind": "opened", "model": "Bomenhof.rvt", "project": "2233 IKC Poeldijk", "revision": "<version guid>" }
```

`kind` is one of `opened` | `saved` | `changed` | `closed`. `DocumentChanged` is throttled to at most one POST per ~45s (it fires per transaction); the others send immediately. Loopback only, short timeout, all errors swallowed — if the orchestrator isn't running it's a silent no-op and Revit never blocks. An optional `X-Loam-Token` header is sent when `LOAM_MODEL_EVENT_TOKEN` is set.

---

## Install (one click)

Download **`install.bat`** from the [latest release](https://github.com/thomhoffer-arch/Mycelium-for-Revit/releases/latest) and double-click it.

The installer auto-detects Revit 2024, 2025, and 2026, downloads the correct build for each version found, installs it to Revit's add-in folder, and registers the MCP server in both **Claude Desktop** (`%APPDATA%\Claude\claude_desktop_config.json`) and **Claude Code** (via `claude mcp add` when the CLI is on PATH).

**MCP URL:** `http://127.0.0.1:47100/mcp`

Launch Revit and open a project — the MCP server starts automatically.

---

## Repo layout

```
src/
  App.cs                            # IExternalApplication entry — boots MCP server
  Mcp/
    McpServer.cs                    # HttpListener + JSON-RPC dispatcher → IPdraTool
  RevitBridge/
    RevitContext.cs                 # ExternalEvent marshalling to UI thread
  Pdra/                             # started as a PDRA vendor drop; Mycelium is the primary connector now —
                                     # see "Repo layout" note below before assuming this is untouchable
    IPdraTool.cs / ToolMetadata.cs / PdraJson.cs / JsonHelpers.cs
    SpineKeys.cs / ElementContextReader.cs
    Tools/
      GetModelRevisionTool.cs
      ListElementsTool.cs
      FilterElementsByScopeBoxTool.cs
      GetElementByUniqueIdTool.cs
      GetElementByIfcGuidTool.cs
      GetDoorRoomsTool.cs
      GetClassificationSourcesTool.cs
  LoamRevitConnector.addin
  LoamRevitConnector.csproj
docs/
  CONTRACT.md                       # field-level wire contract
tools/
  selftest.ps1                      # conformance check against a live MCP endpoint (needs Revit open — not CI)
ROADMAP.md
```

Files under `src/Pdra/` started as a PDRA vendor drop (bootstrap: reuse proven implementations instead of
starting from zero). That no longer means "don't touch" — **Mycelium-for-Revit is the primary connector;
PDRA is a fallback**, so contract-compliance fixes and new capability land directly here, not gated on an
upstream PDRA change. Where a fix is ALSO a genuine PDRA bug (not Mycelium-specific), upstreaming it too is
still worthwhile so the two don't silently diverge on a shared defect — but it isn't a prerequisite.

---

## See also

- [Mycelium Studio](https://github.com/thomhoffer-arch/Mycelium)
- [PDRA (Revit MCP tools — source of `src/Pdra/`)](https://github.com/thomhoffer-arch/PDRA)
