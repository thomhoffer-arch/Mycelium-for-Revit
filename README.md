# Mycelium Studio — Revit Connector

A **Revit connector** for [Mycelium Studio](https://github.com/thomhoffer-arch/Mycelium). Loads as
a Revit add-in and writes the model as an **append-only, checkpointed JSON Lines change log** —
one snapshot when a model opens, then only what changes, numbered and checkpointed. Loam (or any
reader) reads the log directly from disk; nobody calls anyone. See **[docs/MODEL_LOG.md](docs/MODEL_LOG.md)**
for the full on-disk format.

Alongside the log, it also starts an MCP-over-HTTP server on port 47100 for two remaining jobs:
on-demand heavy detail the log deliberately leaves out (full geometry, boundary lines, per-view
visibility), and acting on Revit while the user watches (select/zoom, temporary isolate, open a
sheet) — see docs/MODEL_LOG.md's "Tools" section. The connector's original per-tool read
interface (`list_elements`, `get_rooms`, …, listed below) is **frozen**: still served for
backward compatibility, but gets no new capability and is removed once the log is in active use.

The frozen read-tool implementations are **PDRA's verbatim** (vendored under `src/Pdra/`). One
contract, two front-ends (PDRA and this connector).

---

## On-demand detail and acting on Revit (the tools this connector keeps)

| Tool | What it does |
|---|---|
| `get_element_detail` | For up to 50 elements: `geometry` (triangulated faces), `boundaries` (room/space boundary segments), `views` (where it's visible), `live` (current state, matching the log's own `el` shape) |
| `find_elements` | Spatial/structured query (`box`, `near`, `intersects`, `param`) evaluated live, using Revit's own geometry filters |
| `show_element` | Selects and zooms to an element in the active view |
| `isolate_elements` | Temporary Hide/Isolate in the active view (reversible) |
| `open_sheet` | Opens a sheet by its SheetNumber |

## Frozen read tools

All tools below are read-only, no writes, no transactions. **Frozen**: kept for backward
compatibility, get no new capability, and are removed once the model log (docs/MODEL_LOG.md) is
in active use — reading now happens by tailing the log, not by calling these.

| Tool | What it returns |
|---|---|
| `get_model_revision` | Freshness stamp: `version_guid`, `number_of_saves`, `has_unsaved_changes`, document title and path, plus `worksharing` and the central-model / cloud (C4R) identity fields that stay the same across every user of a shared model |
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

**MCP over Streamable HTTP**, JSON-RPC: `initialize` → `tools/call`. Tool output is a JSON string
in `result.content[0].text`. Bearer auth via `Authorization: Bearer <token>` is **required** —
the server refuses to start without a token.

Configuration lives in the add-in's own **settings file**, never an environment variable:
`%LOCALAPPDATA%\Loam\RevitConnector\settings.json` (`src/RevitBridge/ConnectorSettings.cs`):

```json
{
  "listen": "http://127.0.0.1:47100/mcp",
  "token": "<auto-generated on first run>",
  "modelLogRoot": null
}
```

A missing settings file gets one created automatically on first run, with a fresh random token —
no manual step needed for a normal install. `modelLogRoot` overrides where the model log is
written (default: `%LOCALAPPDATA%\Loam\RevitConnector\model-logs\`). Leaving `token` blank is
treated as a deliberate choice and refused, not silently downgraded to no auth — this replaces an
earlier bug where `README.md` documented `MYCELIUM_REVIT_LISTEN`/`_TOKEN` while `src/App.cs` read
`LOAM_REVIT_LISTEN`/`_TOKEN`, so following the README silently ran the MCP server with no bearer
auth at all. See `docs/CONTRACT.md`'s changelog for that history.

If multiple Revit instances are open, only the first one serves MCP requests — subsequent instances load silently (port already owned).

---

## Event push (additive)

Besides answering MCP calls, the connector **pushes** Revit document events to the orchestrator so it doesn't have to poll. On open / save / sync / change / close it fires a fire-and-forget `POST` to:

```
POST http://127.0.0.1:47600/api/model-event      (override port with LOAM_HTTP_PORT)
{
  "kind": "saved",
  "model": "Bomenhof.rvt",
  "project": "2233 IKC Poeldijk",
  "revision": "<version guid>",
  "worksharing": "file_based_local",
  "central_model_path": "S:\\Central\\Bomenhof_central.rvt",
  "cause": "sync"
}
```

`kind` is one of `opened` | `saved` | `changed` | `closed`. `DocumentChanged` is throttled to at most one POST per ~45s (it fires per transaction); the others send immediately. Loopback only, short timeout, all errors swallowed — if the orchestrator isn't running it's a silent no-op and Revit never blocks. An optional `X-Loam-Token` header is sent when `LOAM_MODEL_EVENT_TOKEN` is set.

`worksharing` (always present — `cloud` | `not_workshared` | `file_based_central` | `file_based_local` |
`file_based_unknown`) and, when known, `central_model_path` / `cloud_project_guid` / `cloud_model_guid` /
`cloud_region` are the cross-user model identity: `model`/`revision` above name *this user's local copy*
of a workshared model, which differs per user even for the same shared model — see `docs/CONTRACT.md`'s
`get_model_revision` section for the full field-by-field rundown (the same fields, same values, both places).

On `kind: "saved"` only, `cause` distinguishes a plain Ctrl+S (`"save"`) from a Sync to Central
(`"sync"`) — both still report `kind: "saved"` so older orchestrator builds keep working; `cause` is
omitted on every other `kind`.

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
  App.cs                            # IExternalApplication entry — settings, MCP server, model-log wiring
  Mcp/
    McpServer.cs                    # HttpListener + JSON-RPC dispatcher → IPdraTool
  RevitBridge/
    RevitContext.cs                 # ExternalEvent marshalling to UI thread
    ConnectorSettings.cs            # settings file (listen/token/modelLogRoot) — replaces env vars
  ModelLog/                         # the model-log writer — its OWN project, Revit-free, unit-tested
    ModelLog.csproj                 # (see tests/ModelLog.Tests/); referenced by LoamRevitConnector.csproj
    ModelLogWriter.cs / LogSegmentWriter.cs / HashCache.cs / RecordHash.cs
    StateStore.cs / WriterLock.cs / IdleSliceRunner.cs / RecordKinds.cs / ModelLogState.cs
  ModelLogCapture/                  # Revit-dependent: builds ModelLog's records from live Revit objects
    RecordBuilder.cs                 # element/type/category/pdef/node/grid/mat/sheet/rev/link field-groups
    ModelLogService.cs                # snapshot-on-open, reconcile-on-open/sync, change-capture-on-idle
  Pdra/                             # started as a PDRA vendor drop; Mycelium is the primary connector now —
                                     # see "Repo layout" note below before assuming this is untouchable.
                                     # FROZEN (see docs/MODEL_LOG.md) except the 5 on-demand/acting tools.
    IPdraTool.cs / ToolMetadata.cs / PdraJson.cs / JsonHelpers.cs
    SpineKeys.cs / ElementContextReader.cs
    Tools/
      GetModelRevisionTool.cs  … (frozen — see README's "Frozen read tools" table)
      GetElementDetailTool.cs / FindElementsTool.cs / ShowElementTool.cs
      IsolateElementsTool.cs / OpenSheetTool.cs        # NOT frozen — the kept tool family
  LoamRevitConnector.addin
  LoamRevitConnector.csproj
tests/
  ModelLog.Tests/                   # unit tests for src/ModelLog/ — no Revit API, runs in CI
docs/
  CONTRACT.md                       # field-level wire contract (the frozen read tools)
  MODEL_LOG.md                      # the model-log on-disk format — the primary spec now
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
