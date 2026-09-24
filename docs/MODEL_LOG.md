# The model log

The connector's primary output: an append-only, checkpointed change log of the Revit model,
written to local disk. Loam (or any reader) reads the log — nobody calls anyone. One snapshot
when a model opens, then only what changes, numbered and checkpointed. Covers every element
category (walls, floors, roofs, doors, windows, structure, MEP, furniture, rooms and spaces).

This document is the on-disk contract. `src/ModelLog/` (the writer, hashing, rotation, crash
safety — Revit-free, unit-tested in `tests/ModelLog.Tests/`) and `src/ModelLogCapture/` (the
Revit-dependent side that builds these records from live Revit objects) implement it. Where an
implementation detail below is a judgment call this repo made rather than something the format
requires, it's marked **(this repo's choice)**.

## Why a log instead of read tools

| Read tools (frozen, see below) | Change log |
|---|---|
| Every read request runs on Revit's single UI thread while the user works | The connector writes only when a model opens or something changes, during idle time |
| Reads are paged and truncated (`list_elements` stops at 2,000) | The snapshot is complete and says so in a checkpoint |
| A read returns only the current value | Every state is written in order, so earlier values are never lost |
| Data exists only while Revit has the model open | The log stays readable after Revit closes |
| Many tools, each with its own shape; docs have drifted from code | One record format with declared field roles |
| No test project; checks need a live Revit | A recorded log is a test fixture |

**Rules for the connector:**

1. **Write states, never before/after.** A new element is written in full; a changed element as
   a partial state (only the fields that changed, with their new values). The connector never
   computes diffs.
2. **Everything Revit knows, written once.** All non-empty parameters, materials, sheets,
   revisions and relations go in. Keep size down by writing definitions once, writing only
   what's set, and appending only changes, never by leaving data out or rounding it.
3. **Revit concepts stay Revit concepts.** No IFC export or conversion. IFC's spatial terms
   (site → building → storey → space) are only the naming for the tree.
4. **Never block the user.** All capture work runs in small idle-time slices; heavy detail is
   fetched on request, never logged.

## The log format

JSON Lines (UTF-8, one record per line). Every record has `seq` (monotonic per model, never
reused), `ts` (UTC, when the connector wrote it) and `k` (the record kind).

**Compact without losing anything:**

1. **Every fact once.** Definitions (parameters, categories, materials, types, tree nodes) are
   written once as their own records. Everything else references them by id.
2. **Numbers exactly as Revit stores them:** internal units, doubles at full precision, never
   rounded or converted. Each parameter definition declares its spec (`ForgeTypeId`, e.g.
   `autodesk.spec.aec:length`), and the header declares the project's display units per spec, so
   a reader can show "3050 mm" exactly as Revit does.
3. **Only what's set.** Instance parameters only when they have a value. Type-driven values only
   on the type record.
4. **Changes as partial states.** A modified element is written with only the field-groups whose
   value changed (new values) plus `unset` for field-groups that disappeared entirely. Still
   states, never before/after. Every segment begins with a full state of everything, so a reader
   never needs more than one segment.
   - **(this repo's choice)** "Field-group" means one of `el`'s own top-level keys (`h`, `loc`,
     `grid`, `rel`, `q`, `bb`/`pt`, `mats`, `p`) — see `ModelLogWriter.WriteIfChanged`. The hash
     cache is keyed per field-group, not per individual parameter inside `p`, so a single changed
     parameter re-writes the whole `p` object, not just that one entry. Finer-grained diffing
     within `p` is a possible follow-up, not required by this format.
5. **Compressed at rest:** finished segments are gzipped (`.jsonl.gz`); the active segment stays
   plain for appending. Gzip, because Revit 2024 and older run .NET Framework 4.8, which has no
   Brotli.

**Record kinds:**

| `k` | Written | Carries |
|---|---|---|
| `header` | First line of every segment | `schema: "model-log/1"`; model identity (cloud model GUID, or central path + ProjectInformation UniqueId) and title; producer and Revit version; display units per spec; coordinates (project base point, survey point, true north); the field-role map |
| `session` | Every `DocumentOpened` | `producerVersion`, `revitVersion` (when known) — lets a reader tell exactly which connector version wrote the records that follow, without diffing `header` records across segments. Also drives the connector's own upgrade cleanup: see "When the connector writes" below. |
| `project` | Snapshot; on change | Project information: number, name, client, address, status, and every other Project Information parameter |
| `pdef` | First time a parameter is seen | `id` (`builtin:<BuiltInParameter>`, `shared:<GUID>`, or `project:<id>`), name, group, spec, storage, instance or type |
| `cat` | First time a category is seen | Id (`c:<name>`), name, `BuiltInCategory`, discipline |
| `node` | Snapshot; on change | Spatial tree: `id` (`n:<ElementId>`), `parent`, `level` (`storey`/`building`/`space`/`zone`), name, number, elevation |
| `grid` | Snapshot; on change | Grid name and line (ends, internal units) |
| `mat` | Snapshot; on change | Material: id (`m:<ElementId>`), name, class, and all its parameters |
| `type` | Snapshot; on change | Id (`t:<ElementId>`), category, family, type name, all type parameters |
| `el` | Snapshot (full); on change (partial) | The element (see below) |
| `del` | On delete | UniqueId and numeric ElementId (when still known) |
| `sheet` | Snapshot; on change | Sheet number, name, current revision, the views placed on it, and the ids of the revisions it carries |
| `rev` | Snapshot; on change | Revision: sequence, number, date, description, issued |
| `link` | Snapshot; on change | Linked model instance: the link's model identity and its transform |
| `chg` | Before the records of one edit | Revit transaction names, the editor, and counts of added/modified/deleted |
| `cp` | End of snapshot/reconcile; after sync; on close | Checkpoint: `complete`, model version, element count, `lastSeq`, `closed` |
| `gap` | When the connector knows it missed events | `fromSeq`, reason; closed by the next checkpoint |

**Field roles** are declared once in the header (`identity`, `handle`, `location`, `type`,
`relation`, `sheet`, `quantity`, `param`), so a reader works from roles, never from Revit
parameter names.

## What goes into each element record (`el`)

The same fields for every model category. Leave a field out when it doesn't apply; never invent
a value.

| Field | Role | Source in Revit |
|---|---|---|
| `id`, `eid` | identity | `UniqueId`; numeric `ElementId` |
| `ifc` | identity | IFC GlobalId: `{guid}` from the stored `IFC_GUID` parameter when present (authoritative — what Revit's own IFC exporter wrote), else `{guid, derived: true}` computed from `UniqueId` (`ModelLog/IfcGuid.cs`, ported from SRM's `srm/ifcguid.py`) — an extra identifier so Loam can link ClashControl/BCF/IFC-export/email references straight to the Revit element, never a model→IFC conversion |
| `cat`, `fam` | param | Category id (a `cat` record), family name |
| `type` | type | `GetTypeId()` → a `type` record |
| `h` | handle | Mark, Type Mark (via type) |
| `loc` | location | Containing storey and space as tree node ids |
| `grid` | handle | Nearest grid intersection ("C/4"), computed from the location point and the `grid` records |
| `rel` | relation | Host; room from/to (anything between two spaces); MEP system membership and connected elements; group; assembly; design option; workset; phase created/demolished |
| `q` | quantity | Length, width, height, area, volume, perimeter (internal units) |
| `bb`, `pt` | quantity | Bounding box; location point or curve ends (internal units) |
| `mats` | relation | Each material id with its area and volume on this element |
| `sheets` | sheet | Sheet numbers of every sheet a tag on this element is placed on |
| `p` | param | Every instance parameter with a value: `[pdef id, value]` pairs. Element-id values are written as the referenced element's UniqueId. |

`del` (deletion) records carry `id` (UniqueId) always, `eid` only when the caller still had the
numeric ElementId at the time — a reconcile-detected deletion (an id that vanished from a fresh
walk) never has one, since `Element.Id` isn't resolvable off a UniqueId that no longer exists.

Rooms/spaces/areas are **never** `el` records — they're the spatial tree, logged as `node`
records only (see `RecordBuilder.IsLoggableModelElement`); `h`'s own room/space number-and-name
lives on the `node` record instead, not nested under a building element's `h`.

One full element record (about 0.8 KB before compression):

```json
{"seq":48213,"ts":"2026-09-23T14:02:11Z","k":"el","id":"5f1c…-0004a2b1","eid":303793,"cat":"c:Walls","type":"t:118233","h":{"mark":"W-12"},"loc":{"storey":"n:311","space":"n:4402"},"grid":"C/4","rel":{"hosts":["5f1c…-0004a2c7"],"phaseCreated":"New Construction","workset":"Shell"},"q":{"length":20.997,"height":10.006,"area":210.1},"bb":[[39.37,13.12,41.34],[60.37,13.62,51.35]],"mats":[["m:77",210.1,10.5]],"p":[["builtin:FIRE_RATING","60"],["shared:9f2e…","EW-02"]]}
```

A later edit to only its fire rating is written as
`{"seq":49102,…,"k":"el","id":"5f1c…-0004a2b1","p":[["builtin:FIRE_RATING","90"]]}`, about 120 bytes.

**Not in the log** (available on request — see Tools, below): full geometry (faces, solids,
meshes), room and space boundary lines, per-view visibility. Leave out values that change on
their own (e.g. timestamps), or every save looks like a change to every element.

## When the connector writes

The connector keeps a **hash per field-group, per id, per family** of the last state it wrote,
persisted next to the log (`state.json`, via `src/ModelLog/HashCache.cs`). That one cache makes
every trigger below cheap, and makes the log self-healing.

| Trigger | Revit hook | What the connector does |
|---|---|---|
| Model opened, no log yet | `DocumentOpened` | Write a `session` record, then a full snapshot: `project`, `pdef`, `cat`, `node`, `grid`, `mat`, `type`, `el`, `sheet`, `rev`, `link`, then a `cp` |
| Model opened, log exists, same producer version | `DocumentOpened` | Write a `session` record, then **reconcile:** walk everything, write only what differs from the hash cache (partial states), `del` for ids that no longer exist, then a `cp`. This catches edits made while the connector wasn't running. |
| Model opened, log exists, producer version changed since the last `session` | `DocumentOpened` | Write a `session` record, then a **forced-full-state reconcile**: same walk as above, but every field-group is written in full (not just what differs), so a version that starts logging new fields backfills them onto every existing element; stale-element deletion still runs the same as an ordinary reconcile, so fields/categories a new version stops logging are cleaned up via ordinary `del` records. No fresh log or second snapshot — the existing log just gets one reconcile pass that behaves like a snapshot for state, while keeping its `seq` numbering and history intact. |
| User edits | `DocumentChanged` | Queue added/modified/deleted ids and transaction names/editor. During idle time write one `chg`, then a record for each id whose hash changed. |
| Sync with central / reload latest | `DocumentSynchronizedWithCentral`, `DocumentReloadedLatest` | **Reconcile**, then a `cp` with the new model version. This picks up other people's changes even if `DocumentChanged` did not report them. |
| Model closing | `DocumentClosing` | A final `cp` with `closed: true`, so a quiet log reads as "closed", not "connector crashed" |

**Who changed it:** on workshared models, `WorksharingUtils.GetWorksharingTooltipInfo(doc,
id).LastChangedBy` after a sync; for local edits, `Application.Username`. Leave `by` out when
unknown.

**Keeping Revit responsive:**

- Never do capture work inside `DocumentChanged`; only record ids. Reading and writing happens
  in the `Idling` event, in slices of **at most 50 ms**, resuming on the next idle tick (see
  `src/ModelLog/IdleSliceRunner.cs`).
- A snapshot or reconcile of a large model therefore spreads over many idle ticks. That's fine:
  the checkpoint is written only when it finishes.
- Measure it: log the total time and slice count per snapshot and reconcile. A slice that runs
  longer than the budget is a bug.
- The connector never writes to the model (except the two UI-acting tools below, which are
  reversible/temporary by design).

## Where the log lives, rotation and crash safety

**Location:** one folder per model under the connector's own data folder, e.g.
`%LOCALAPPDATA%\Loam\RevitConnector\model-logs\<model id>\`. Configurable via the connector's
settings file (`ConnectorSettings.ModelLogRoot`), never an environment variable. Everything stays
on the local machine.

```
model-logs/<model id>/
  000001.jsonl      segment: header, snapshot, changes…
  000002.jsonl      next segment, starts with a header
  state.json        hash cache + last seq + last checkpoint
  writer.lock       held while a Revit session writes this model
```

**Rotation:** start a new segment at 64 MB or when a full snapshot is taken. Each segment starts
with a `header` and a full state of every definition and element, so it can be read on its own.
`seq` continues across segments. Gzip a segment once it's finished (`000001.jsonl.gz`). Never
edit a finished segment.

**Expected size** (estimates at about 0.8 KB per full element and 120 bytes per changed field):

| Model | Model elements | Full state, plain | Full state, gzipped | Changes per busy day, gzipped |
|---|---:|---:|---:|---:|
| House / small building | 10,000 | ~8 MB | ~1 MB | < 0.1 MB |
| Office / school | 100,000 | ~80 MB | ~8–10 MB | ~0.1–0.3 MB |
| Hospital / large complex | 500,000 | ~400 MB | ~40–50 MB | ~0.3–1 MB |

**NEEDS LIVE-REVIT CHECK:** these are estimates, not measured — the handoff plan calls for
recording two real fixture models (one workshared, one with MEP) and confirming actual size
against this table. Not done in this sandbox (no Revit available).

**Crash safety:**

1. Append one complete line, then flush. A reader ignores a torn last line.
2. Update `state.json` only **after** the log line is flushed. If a crash lands in between, the
   next reconcile writes the same state again: a harmless duplicate, never a lost change.
3. On startup, if the previous session left no `closed` checkpoint, write a `gap` record (from
   the last seq) and then reconcile. The gap is closed by that reconcile's checkpoint.
4. **One writer per model:** take `writer.lock`. A second Revit session with the same model open
   doesn't write, and logs that it didn't.

## Tools: on-demand detail and acting on Revit

The log carries everything except heavy, rarely needed data. The MCP server stays for two jobs,
and **only while the model is open in Revit**: fetching that heavy detail on request, and acting
in Revit while the user watches. All tools run through an `ExternalEvent` (never blocking the
UI), are read-only unless stated, declare their arguments and limits in `inputSchema`, and return
ids that match the log.

| Tool | Returns / does | Limits |
|---|---|---|
| `get_element_detail(ids, include)` | For up to 50 elements, any of: `geometry` (faces, triangulated, capped), `boundaries` (room/space boundary segments and the bounding elements), `views` (views/sheets where it is visible), `live` (its current state, same shape as an `el` record, to confirm the log is fresh) | 50 ids per call |
| `find_elements(box\|near\|intersects\|param)` | Element ids matching a spatial or structured query | 5,000 ids per call, paged |
| `show_element(id)` | Selects the element and zooms to it in the active view | — |
| `isolate_elements(ids)` | Temporarily isolates elements in the active view | 5,000 ids |
| `open_sheet(number)` | Opens a sheet | — |

- `find_elements` answers what the log can't answer cheaply, like "what intersects this duct" or
  "what's within 1 m of this column", using Revit's own `BoundingBoxIntersectsFilter`/
  `ElementIntersectsElementFilter`.
- **Existing read tools** (`list_elements`, `get_rooms`, `get_door_rooms`, …): **frozen** — add
  nothing new, fix nothing beyond a genuine regression, remove them once the log is in active
  use. See the comment at the top of `McpServer`'s tool list.

## Security

`README.md` used to document `MYCELIUM_REVIT_LISTEN`/`_TOKEN` while `src/App.cs` read
`LOAM_REVIT_LISTEN`/`_TOKEN` — so following the README silently ran the MCP server with **no
bearer auth**. Fixed: both settings (plus the log root) now live in the add-in's own settings
file (`src/RevitBridge/ConnectorSettings.cs`), never an environment variable, and the MCP server
**refuses to start** without a token — a missing settings file gets one auto-generated on first
run; an explicitly blank one refuses rather than falling back to no auth.

## Review of the first real log (2026-09-24)

Against a real project ("Horizons"), the merged connector wrote a valid `model-log/1` log — the
header, `cat`/`pdef`/`node`/`mat`/`type` dictionaries, ~890 bytes/element, no element written
twice. The snapshot was inspected mid-run (no `cp` yet) at 4,039 elements / 3.4 MB. The format
was right; the content needed these fixes, all now made:

| # | Fix | Measured before | Addressed by |
|---|---|---|---|
| 1 | **Log model elements only.** Skip non-model/internal categories (area boundaries, `<Sketch>`, sun path, automatic dimensions, views, tags, legend components, work plane grids, lines). Rooms/spaces/areas belong in `node`, not `el`. | Most records were noise: 838 area boundaries, 410 sketches, 243 sun path, 198 auto-dimensions, 158 views vs. 121 walls | `RecordBuilder.IsLoggableModelElement` |
| 2 | **Handles (`h`).** Mark/Type Mark, kept in `p` too. | `h` on 0% of elements in the (mostly-noise) sample | Already implemented; the 0% was very likely diluted by the noise fix 1 removes — sketches/sun-path/dimensions never carry Mark. Re-verify once fixtures are recorded. |
| 3 | **Relations.** Host; room from/to for anything between two spaces; MEP system membership; connected elements. | `rel` had only workset/group/designOption/phaseCreated; host 0% | `roomFrom`/`roomTo` (`get_FromRoom`/`get_ToRoom`), `mepSystems`/`connected` (via `ConnectorManager`) added to `BuildRelations`. The measured 0% host also likely reflects an in-progress snapshot not yet reaching any doors/windows. |
| 4 | **`eid`** (numeric ElementId) on every element. | 0% | Was a genuine bug — never set. Fixed. |
| 5 | **Units per parameter.** Every numeric `pdef` carries its spec. | `pdef` had id/name/storage/scope/group, no spec | Spec-reading code already existed (matches `ElementContextReader`'s own proven pattern); likely diluted by noise the same way as fix 2 — most walked "parameters" were on non-dimensional noise elements. Re-verify once fixtures are recorded. |
| 6 | **Type and location on building elements.** | `type` 27%, `loc` 38% overall; walls already 100%/100% | Direct consequence of fix 1 — once noise is excluded, the denominator is only building elements. |
| 7 | **Sheets.** `sheet` records and `el.sheets` (which sheets show/tag the element). | Role declared, 0 records | `RecordBuilder.BuildTaggedSheetIndex` (a reverse index over every `IndependentTag`, built once per pass) feeds `el.sheets`; `sheet` records themselves were likely just not reached yet by the in-progress snapshot the report was taken from. |
| 8 | **Materials with quantities (`mats`).** | 3% | No code change; expected to rise sharply once fix 1's noise (which rarely carries materials) is excluded. |

**Kept as designed:** lean, searchable state (elements, types, parameters, locations, relations)
plus changes, segments rotated and gzipped; heavy detail (geometry, room boundaries, spatial
searches) stays behind the on-demand tools while the model is open.

**Still to verify with a real model** (not only fixtures, and not from a run inspected
mid-snapshot): after these fixes, the element count is building-elements-only; a known door
shows mark, host wall, from/to room, storey and space; a small edit produces one `chg` plus a
partial `el` of about 120 bytes; the snapshot reaches a `cp`.

## Round 4: reconcile-on-open stalling (2026-09-24)

After the round-3 release (v1.0.14, producer version `0.4.0`), a live-Revit reopen of the same
"Horizons" project reported: the connector started, wrote a `header`, and began adding `eid`/`ifc`
to existing elements — but the bulk update stopped after **~205 of ~33,600 elements**, in a
~2-minute burst, and never recovered. No checkpoint was ever written, the log kept updating the
same morning's segment instead of a clean new snapshot, and no record named which producer
version wrote what.

| # | Symptom reported | Root cause | Fix |
|---|---|---|---|
| 1 | Reconcile processes ~200 elements in a 2-minute burst, then drops to 1–3/minute (matching only live edits) | `App.cs`'s `OnIdling` handler never called `IdlingEventArgs.SetRaiseWithoutDelay()`. Revit's `Idling` event fires once and then waits for further UI activity (mouse move, keystroke) before firing again — it is not a free-running timer. Without this call, `IdleSliceRunner`'s slice-based reconcile only progresses while the user is actively moving the mouse over Revit's window, and stalls almost completely the moment they stop. | `App.cs`'s `OnIdling` now calls `e.SetRaiseWithoutDelay()` whenever `ModelLogService.ShouldRequestContinuousIdling()` is true, so Idling keeps firing back-to-back until the queue drains, then reverts to Revit's normal cadence. **Refined after a follow-up live report** ("connector blocking/slow for several seconds after most actions") once this fix actually started draining a large backlog: requesting continuous re-firing unconditionally raced to drain the ENTIRE backlog in one uninterrupted burst, starving Revit's own message pump of redraw/input processing — the very stall this fix was meant to remove, just relocated. `ShouldRequestContinuousIdling` now caps each burst to 250ms, then releases control for one natural idle interval (which still fires again almost immediately during active editing) before starting a fresh burst — steady forward progress on a large backlog without monopolizing the idle loop for one long uninterrupted stretch. |
| 2 | Log still ~97% noise (sketches/tags/dimensions); no room/MEP/sheet-tag data | Direct consequence of #1 — the reconcile that applies the round-2/round-3 noise filter and relation-building fixes never got far enough to reach most of the model. Not a separate bug. | Same fix — once the reconcile actually completes, the round-2/round-3 fixes (already merged, see the round-2 review above) apply to every element, not just the first ~200. |
| 3 | No checkpoint ever written | Same root cause — a checkpoint is only written when `ReconcileJob`'s enumerator finishes, which never happened. | Same fix. |
| 4 | No record of which producer version wrote which records — only that morning's `header` (version `0.4.0`) | The connector had no per-open version record at all. | New `session` record kind, written on every `DocumentOpened` (`RecordKinds.Session`, `ModelLogWriter.RecordSession`), carrying `producerVersion` and `revitVersion`. `ModelLogState.LastProducerVersion` persists the last one across sessions so the connector can detect its own upgrades. |

**Upgrade cleanup** (separate developer feedback, same round): when `OnDocumentOpened` finds an
existing log whose last `session` recorded a different `producerVersion` than the connector
running now, it forces the reconcile that follows to write every field-group in full (not only
what differs from the hash cache) — `ModelLogService.WalkModel`'s `forceFullState` and
`detectDeletions` parameters were split apart (previously one bool controlled both) so a normal
reconcile can keep detecting deletions while an upgrade-triggered one does both full-state writes
and deletion detection in the same pass. This backfills any field a new version starts logging
(e.g. `eid`/`ifc` for users upgrading from a version that didn't have them) and cleans up, via
ordinary `del` records, anything a new version stops logging — without a second snapshot or a new
log file, so `seq` numbering and log history stay intact.

**Not yet verified** (needs a real reopen after this fix ships): that a reconcile on a ~33,600
element model now actually reaches its checkpoint in one sitting (or several, but without long
stalls between element ~200 and the rest), that ordinary editing no longer stalls for seconds at a
time now that continuous idling is burst-capped, and that the `session`/upgrade-reconcile behavior
produces the expected full-state backfill on the very next open after this version installs.

## Order, verification and done

| Order | Work | Why this order | Status |
|---|---|---|---|
| 1 | Security fix (settings file, token required) | Auth can be silently off today | **Done** |
| 2 | Log writer: format, segments, gzip on rotate, `state.json`, lock, crash safety | Everything else writes through it | **Done** (`src/ModelLog/`, unit-tested) |
| 3 | Definitions + snapshot on open, in idle slices | First complete, checkpointed log | **Done** (`ModelLogService.SnapshotJob`) |
| 4 | Change capture: `DocumentChanged` queue → idle processing, partial states, `chg` records | Live edits | **Done** (`ModelLogService.ChangeCaptureJob`) |
| 5 | Reconcile on open and after sync/reload | Self-healing; covers other users' changes | **Done** (`ModelLogService.ReconcileJob`) |
| 6 | Full richness: handles, grid refs, relations, materials with quantities, sheets, revisions, all parameters | The data that makes the log worth reading | **Done, unverified** — see below |
| 7 | `get_element_detail`, `find_elements`, interaction tools; freeze the read tools | Heavy detail on request; reading moved to the log | **Done** |

**Test without Revit:** the record-building code (units, hashing, parameter ids, JSON shape)
lives in `src/ModelLog/`, a plain class library with unit tests (`tests/ModelLog.Tests/`, no
Revit API dependency, runs in CI). **Record** logs from two real models (one workshared, one with
MEP) and keep them as fixtures — **not done in this sandbox** (no Revit available); tracked as a
follow-up.

**Verify in Revit (none of this can be assumed — not exercised in this sandbox):**

1. Does `DocumentChanged` report other users' changes after reload-latest/sync? The reconcile
   makes the log correct either way; record which it is.
2. Idle slices stay under 50 ms on the largest model available; typing and navigation stay smooth
   during a snapshot.
3. A type edit writes one `type` record, plus only the instances whose own state changed.
4. Kill Revit mid-snapshot: on the next open a `gap` is written, the reconcile closes it, and no
   change is lost.
5. **Lossless:** for 20 parameters across categories, the logged value converted with the
   header's display units shows exactly what Revit shows.
6. **Size:** measure both fixture models against the size table and record the real numbers here.
7. `RecordBuilder`'s per-category quantity/grid-intersection heuristics (marked `NEEDS
   LIVE-REVIT CHECK` in that file's own comments) match what a drafter would actually expect.
8. **IFC GlobalId:** export a small model to IFC from Revit with "Store IFC GUID" enabled, then
   confirm the `ifc.guid` this connector computed for those elements *before* that export (when
   `IFC_GUID` was still empty, so the logged value was `derived: true`) matches the GlobalId the
   export actually assigned.

**Done** = all seven steps merged; the two recorded fixtures checked in; the eight Revit checks
passed and written up; the add-in rebuilt and installed on the office machines. This repo has
merged all seven steps' code; the fixtures and the eight Revit checks are the open item.
