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
   states, never before/after. Every log GENERATION begins with a full state of everything; a
   generation may continue over further size-rotated segments (see "Rotation"), so a reader
   rebuilds the current state from the newest generation's first segment forward.
   - **(this repo's choice)** "Field-group" means one of `el`'s own top-level keys (`h`, `loc`,
     `grid`, `rel`, `q`, `bb`/`pt`, `mats`, `p`) — see `ModelLogWriter.WriteIfChanged`. The hash
     cache is keyed per field-group, not per individual parameter inside `p`, so a single changed
     parameter re-writes the whole `p` object, not just that one entry. Finer-grained diffing
     within `p` is a possible follow-up, not required by this format.
5. **Compressed at rest:** finished segments are gzipped (`.jsonl.gz`); the active segment stays
   plain for appending. Gzip, because Revit 2024 and older run .NET Framework 4.8, which has no
   Brotli. Compression itself runs on a background task, not Revit's UI thread — see "Rotation"
   below.

**Record kinds:**

| `k` | Written | Carries |
|---|---|---|
| `header` | First line of every segment | `schema: "model-log/1"`; `modelId` (the STABLE identity: cloud project+model GUID or central path — same value the log folder is named from), `title` (the local file title, display only — differs per user, e.g. carries a Windows username, never used as identity), plus `cloudProjectGuid`/`cloudModelGuid`/`centralModelPath`/`modelInstanceId` when known; producer and Revit version; display units per spec; coordinates (project base point, survey point, true north angle, and `sharedTransform` — `ActiveProjectLocation.GetTotalTransform()`'s origin/basisX/basisY/basisZ, mapping this model's internal coordinates into shared coordinates); the field-role map; `segment` (this segment's own number), `generationStart` (the segment holding this log generation's full state) and — on a size-rotated continuation segment only — `continuation: true` (see "Rotation") |
| `session` | Every `DocumentOpened` | `producerVersion`, `revitVersion` (when known) — lets a reader tell exactly which connector version wrote the records that follow, without diffing `header` records across segments. Also drives the connector's own upgrade cleanup: see "When the connector writes" below. |
| `project` | Snapshot; on change | Project information: number, name, client, address, status, and every other Project Information parameter |
| `pdef` | First time a parameter is seen | `id` (`builtin:<BuiltInParameter>`, `shared:<GUID>`, or `project:<id>`), name, group, spec (the parameter's `Definition.GetDataType()`, e.g. `autodesk.spec.aec:length-2.0.0` — looked up in `header.units` for the display unit; never the unit itself), storage, instance or type |
| `cat` | First time a category is seen | Id (`c:<name>`), name, `BuiltInCategory`, discipline |
| `node` | Snapshot; on change | Spatial tree: `id` (`n:<ElementId>`), `parent`, `level` (`storey`/`building`/`space`/`zone`), name, number, elevation |
| `grid` | Snapshot; on change | Grid name and line (ends, internal units) |
| `mat` | Snapshot; on change | Material: id (`m:<ElementId>`), name, class, and all its parameters |
| `type` | Snapshot; on change | Id (`t:<ElementId>`), category, family, type name, all type parameters |
| `el` | Snapshot (full); on change (partial) | The element (see below) |
| `del` | On delete, for EVERY family (`el`/`type`/`node`/`grid`/`mat`/`sheet`/`rev`/`link`) | Id (that family's own id scheme) and numeric ElementId (when still known); `of` names the family, omitted for `el`; `reason`: `deleted` (gone from the model — a real deletion), `filtered` (still in the model, no longer logged in this family — e.g. an upgrade's noise filter), `unreferenced` (a `type` still in the model that no logged element uses). Only `deleted` is a deletion in the model. |
| `sheet` | Snapshot; on change | Sheet number, name, current revision, the views placed on it, the ids of the revisions it carries, and `elements` — UniqueIds of every element tagged or dimensioned in a view placed on it (capped at 500) |
| `rev` | Snapshot; on change | Revision: sequence, number, date, description, issued, `sheets` (sheet numbers carrying it), `clouds` (RevisionCloud UniqueIds tagged with it) |
| `link` | Snapshot; on change | Linked model instance: the link's model identity and its transform |
| `chg` | Before the records of one edit | Revit transaction names, the editor, and counts of added/modified/deleted |
| `cp` | End of snapshot/reconcile; after sync; on close | Checkpoint: `complete`, model version, `modelSaves` (`DocumentVersion.NumberOfSaves` — additive alongside model version, the handoff's "version = GUID + number"), element count, `lastSeq`, `closed`; `errors` (only when above 0: records the pass skipped because Revit threw while reading them) |
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
| `rel` | relation | Host; `hosted` (ids of elements THIS one hosts — the reverse of `host`, e.g. a wall's own hosted doors/windows); room from/to (`roomFrom`/`roomTo`, anything between two spaces: Revit's own From/To Room in the last phase; for doors and windows also the instance's own created phase and then every other phase, and as a last resort a point sampled just beyond each face, marked `roomSource: "geometric"`); MEP system membership and connected elements; group; assembly; design option; workset; phase created/demolished. No `rel.link`: this log only ever walks the HOST document's own elements, never a linked document's — a link itself is its own `link` record kind, so there is no "element belongs to a link" membership to report here. |
| `q` | quantity | Length, width, height, area, volume, perimeter, `thickness` (walls/floors/roofs/ceilings) (internal units) |
| `bb`, `pt` | quantity | Bounding box; location point or curve ends (internal units) |
| `mats` | relation | Each material id with its area and volume on this element |
| `sheets` | sheet | Sheet numbers of every sheet this element appears on: a model view placed on the sheet (plan, ceiling plan, section, elevation, callout, 3D) shows it — Revit's own per-view visibility, `FilteredElementCollector(doc, viewId)` — or a tag on it is placed on the sheet. The visibility part is rebuilt only by a full walk (open/sync without an incremental baseline, new generation); between those, `sheets` is left as last written, never recomputed from tags alone |
| `p` | param | Every instance parameter with a value: `[pdef id, value]` pairs. Element-id values are written as the referenced element's UniqueId. |

`del` (deletion) records carry `id` (that family's own id — a UniqueId for el/grid/sheet/rev/link,
`"prefix" + ElementId` for type/node/mat) always, `eid` only when the caller still had the numeric
ElementId at the time — a reconcile-detected deletion (an id that vanished from a fresh walk)
never has one for `el`, since `Element.Id` isn't resolvable off a UniqueId that no longer exists,
but does for every other family (their id already carries the ElementId, plainly recovered).
`of` names the family (`type`, `node`, `grid`, `mat`, `sheet`, `rev`, `link`) and is omitted only
for `el`, so a reader that only ever expected `el` deletions is unaffected. A `type` counts as
deleted once no logged element references it any more — not necessarily because the `ElementType`
itself was removed, also when every element that used it was itself deleted/retyped — this is
intended: a `type` record nothing points at is dead weight either way.

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
| Model opened, log exists, same producer version, generation not due for renewal | `DocumentOpened` | If the active segment is past 64 MB, a size-based continuation rotation first (see "Rotation"). Write a `session` record, then **reconcile.** Incremental when possible: if the last `cp` set a trusted baseline (see "Incremental reconcile" below), `Document.GetChangedElements` gives exactly what changed and only those ids (plus `del` for real deletions) are written. Otherwise (no baseline, the baseline GUID is rejected, or the diff touches something whose change can silently affect OTHER elements — a Grid, Level, Room/Space/Area, ViewSheet, Viewport, Phase or DesignOption) falls back to the FULL walk: write only what differs from the hash cache (partial states), `del` for every id that no longer exists, same as before. Either way ends in a `cp`. This catches edits made while the connector wasn't running. |
| Model opened, log exists, producer version changed since the last `session`, OR the current generation already spans its maximum number of continuation segments (`ModelLogWriter.MaxContinuationSegments`, 4) and the last one is full | `DocumentOpened` | Start a **new log generation**: rotate to a fresh segment (if the active one has content), write a `header`, `session` and `project` record, re-emit every `pdef`/`cat` definition (their "seen" sets are cleared — a stale/wrong definition can otherwise never be corrected, since they're normally written once and never re-checked), then the full state of everything (same as a first-time snapshot) plus deletion detection, then a `cp`. `seq` numbering and history are kept intact — this is a new segment, not a new log folder. Deletion detection's `del` records carry a `reason` — after an upgrade most are `filtered`/`unreferenced` cleanup, not deletions. |
| User edits | `DocumentChanged` | Queue added/modified/deleted ids and transaction names/editor. During idle time write one `chg`, then a record for each id whose hash changed — held back entirely while a snapshot/reconcile for that document is still running (it reads every element fresh anyway); an id already covered by the walk is dropped once it finishes, unless it was edited again after the walk started, in which case it's kept and still change-captured. After each batch, a size-based continuation rotation if the segment is past 64 MB. |
| Sync with central / reload latest | `DocumentSynchronizedWithCentral`, `DocumentReloadedLatest` | **Reconcile** (or a new log generation instead, by the same rule above), then a `cp` with the new model version. This picks up other people's changes even if `DocumentChanged` did not report them. A snapshot/reconcile already running is **not restarted**: it pauses for the sync, resumes, ends in a `cp` with `complete: false` (part of it was read before the sync), and queues a follow-up reconcile whose `cp` is `complete: true`. |
| Model closing | `DocumentClosing` (and, as a safety net, add-in shutdown for any model still open) | A final `cp` with `closed: true`, so a quiet log reads as "closed", not "connector crashed". `state.json` is compacted at that point, so the file itself (not only its journal) reads `LastCheckpointClosed: true`. |

**Who changed it:** on workshared models, `WorksharingUtils.GetWorksharingTooltipInfo(doc,
id).LastChangedBy` after a sync; for local edits, `Application.Username`. Leave `by` out when
unknown.

**Incremental reconcile:** a full reconcile re-reads every element just to compare hashes —
wasteful once the model is large and only a handful of elements changed since the last open or
sync. `ModelLogWriter.LastCompleteModelVersion` tracks the one thing that makes a shortcut safe:
a model version (`cp.modelVersion`) a checkpoint confirmed the log matches EXACTLY — set only
when that checkpoint was both `complete` and the document had no unsaved changes at that moment
(`doc.IsModified == false`), and cleared on anything else (an interrupted pass, an edit made
since, or a new log generation) — otherwise a user could edit, close without saving, and reopening
would wrongly trust a log that already contains changes the saved file doesn't have.
`ReconcileJob` hands that GUID to `Document.GetChangedElements`; on success (it throws for a GUID
this document doesn't recognize — a different local copy, most likely — caught, not propagated)
the diff's created/modified ids are each processed exactly as `ChangeCaptureJob` processes a live
edit (same shared per-id write path, `ModelLogService.WriteOneElement`), and deleted ids are
matched against the hash cache's own known `el` UniqueIds by their numeric tail (a UniqueId's hex
after its last `-` — `src/ModelLog/UniqueIdElementId.cs`), since a deleted ElementId never
resolves to anything Revit will hand back a UniqueId for. Falls back to the full walk whenever no
baseline exists, the GUID is rejected, the diff touches a Grid, Level, Room/Space/Area,
ViewSheet, Viewport, Phase or DesignOption (any of those can silently change OTHER, untouched
elements' own derived fields without touching their own hash), OR — on the very first reconcile
after an open — the PREVIOUS session didn't close cleanly (no closed `cp`, the same case that
already writes a `gap`): a crash can leave live-edit records in the log that were never followed
by a checkpoint, so the baseline's `modelVersion` is still the one from BEFORE those edits while
`GetChangedElements` would report nothing changed — only a full reconcile re-derives the truth
then. A later, in-session reconcile (after a sync) is unaffected by this last rule; only that
first post-open one is ever gated by it. **NEEDS LIVE-REVIT CHECK:** whether
a local copy's (as opposed to the central/cloud model's) own version history survives closing and
reopening Revit at all — if it doesn't, `GetChangedElements` simply throws every time and every
reconcile falls back to full, which is still correct, just not faster.

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
  state.json        hash cache + last seq + last checkpoint (base, rewritten only on compaction)
  state.<gen>.jsonl changes to state.json since that base (append-only journal)
  writer.lock       held while a Revit session writes this model
```

**Rotation:** two kinds, both starting the new segment with a `header`. `seq` continues across
segments.

- **New generation** (first open, producer-version change, or the current generation has reached
  `ModelLogWriter.MaxContinuationSegments` continuation segments — checked at open/sync): the
  new segment carries a full state of every definition and element; `header.generationStart`
  equals its own `segment`.
- **Continuation** (size-based, **(this repo's choice)** since v0.6.1): once the active segment
  is past 64 MB, at the next record boundary between passes — after a live-edit batch, after a
  snapshot/reconcile checkpoint, or at open/sync before the session's records — the segment is
  closed (gzipped) and the next one starts with a `header` carrying `continuation: true` and
  `generationStart`, followed simply by the next records. No full state is re-written: doing so
  on every rotation would multiply the log's size, the opposite of what rotation is for. Never in
  the middle of a snapshot/reconcile walk, so a very large model's full state can still run past
  64 MB within its own segment.

A reader rebuilds the current state by reading from the newest header's `generationStart`
forward. (Before v0.6.1 the only rotation was a new generation, at open/sync; a heavy session
could grow the live segment well past 64 MB — 72 MB+ in the 25 Sep review.) A finished segment is gzipped (`000001.jsonl.gz`) off the UI thread:
writing resumes in the next segment immediately, while a background task writes
`000001.jsonl.gz.tmp`, renames it to `.gz`, then deletes the plain file — a reader (or a crash)
only ever sees the complete plain file or the complete `.gz`, never a half-written one of either.
Never edit a finished segment.

**Retention:** at writer startup (the owning session only), finished (`.gz`) segments older than
`ConnectorSettings.ModelLogRetentionDays` (default 90; 0 or less disables this) are deleted — never
the active segment (it's never gzipped), and never any segment of the current generation
(`state.json`'s `GenerationSegment` onward — the generation's full state and every continuation a
reader replays on top of it), regardless of age. When the generation start isn't known yet (a
`state.json` from before v0.6.1), nothing is deleted until the next generation records it. Older
segments are redundant history, not a correctness requirement.

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

1. Append complete lines only. A reader ignores a torn last line. **(this repo's choice)** The
   log is flushed once per idle slice (always before the state journal, so rule 2 holds) and
   at every checkpoint, rotation and close, not after every line. If Revit dies mid-slice, at
   most that slice's lines are lost. The next reconcile writes them again, because the state
   never got ahead of the log.
2. Update the state only **after** the log line is flushed. If a crash lands in between, the
   next reconcile writes the same state again: a harmless duplicate, never a lost change.
   **(this repo's choice)** The state is a base file plus an append-only journal. Each changed
   hash is appended to `state.<gen>.jsonl`, flushed once per idle slice. `state.json` is only
   rewritten (atomically, via `File.Replace`) at a checkpoint, rotation or close, and only when
   the journal is larger than max(1 MiB, a quarter of the base). On open, `seq` and the active
   segment are recovered from the log itself, so a state that lags the log never reuses a
   `seq`.
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

## Round 5: Revit crash during Save to Central (2026-09-24)

Windows' crash log for a Revit crash during a Synchronize with Central ("Save to Central" step)
named the connector: an `AccessViolationException` (reading memory that was no longer valid) with
the stack `App.OnIdling → ModelLogService.OnIdling → IdleSliceRunner.RunSlice →
ModelLogService.ReconcileJob → ModelLogService.WalkModel → FilteredElementIterator.MoveNext`. No
other add-in was in the chain. Revit can't catch that kind of error, so the whole program closed.

| # | Cause | Fix |
|---|---|---|
| 1 | `WalkModel` looped `foreach` over live `FilteredElementCollector`s with a `yield` inside the loop, so a walk paused between idle ticks kept Revit's native element iterator open while the document changed underneath it. | Each section takes its id list in one call (`ToElementIds()`, no `yield` in between), then walks the plain list, looking each element up fresh with `doc.GetElement` and skipping any that no longer exist. |
| 2 | `Idling` fires **during** Save to Central (the earlier assumption that it can't fire mid-command was wrong), so the paused walk was resumed while the sync was rebuilding the model. | `App.cs` now handles the pre-events `DocumentSynchronizingWithCentral`, `DocumentReloadingLatest`, `DocumentSaving` and `DocumentSavingAs`: all model-log work pauses (`ModelLogService.BeginDocumentBusy`, a nesting count) until the matching post-event. A sync, reload or close also bumps a per-document generation. Every job carries the generation it was queued at and checks it, plus `Document.IsValidObject`, before each step, stopping without a checkpoint if either changed. The sync/reload post-event queues a fresh reconcile, or re-runs the snapshot if a brand-new log's first snapshot was the one interrupted. *(Changed in Round 13: only a close bumps the generation now; a sync/reload pauses the in-flight pass and it resumes afterwards — restarting it on every sync meant a busy model's pass never reached its `cp`.)* |
| 3 | An exception from any job propagated out of `IdleSliceRunner.RunSlice`. | `RunSlice` catches it, drops the job and reports it (`onJobFailed`, unit-tested); `App.OnIdling` and the document-event handlers are wrapped too. This covers ordinary exceptions only: .NET 8 never lets managed code catch an `AccessViolationException`, and .NET Framework 4.8 doesn't by default. Fixes 1 and 2 prevent the crash itself. |

Found while re-checking this fix:

- `ChangeCaptureJob` cleared its "queued" flag only at the very end, so a job that stopped early or threw would have stopped live-edit capture for that document for the rest of the session. The flag is now cleared in a `finally`.
- An upgrade's full-state reconcile, or a new log's first snapshot, that a sync interrupted was replaced by an ordinary reconcile. That would skip the backfill, or leave a new log with no `header`/`project` records. Both are now carried over until they actually finish.
- A brand-new log's first line was the `session` record, not the `header` (a round-4 slip). The session record is now written right after the snapshot's header.
- Closing the model while a snapshot or reconcile was unfinished wrote `complete: true`. It now writes `complete: false`.

**Not yet verified:** checks 9 and 10 below.

## Round 6: state file rewritten on every record (2026-09-24)

`ModelLogWriter.Append` rewrote the whole `state.json` (the hash cache: about 26 MB for 33,600
elements) after every record, and every edit batch wrote a `chg` record even when nothing that
gets logged had changed. Disk writes grew with the square of the model size.

Measured with `src/ModelLog` itself on synthetic elements shaped like real ones (13 field groups,
40 parameters). Each scenario ran on a fresh copy of the same 33,600-element log:

| Scenario | Before | After |
|---|---|---|
| Full snapshot | 1,994 s, 434,003 MB written | 6.4 s, 103 MB written |
| Reconcile, nothing changed | 6.1 s, 51.7 MB | 4.3 s, 0.0 MB |
| 100 edits touching nothing logged | 13.2 s, 2,583 MB | 0.6 s, 0.0 MB |
| 100 edits changing one element each | 22.8 s, 5,166 MB | 0.7 s, 0.2 MB |

Fixed at the root, not throttled. The state is now a base file plus an append-only journal (see
crash-safety rule 2 above). A batch's `chg` record is held until the batch actually writes a
record (`ModelLogWriter.BeginChange`/`EndChange`). `state.json` is written compactly. The timings
above exclude Revit's own per-element work, so they isolate the connector's file I/O.

**Not yet verified:** the same measurement on a copy of a real log folder, and Revit's
responsiveness during a snapshot with this change.

## Round 7: parameter unit lookup broken (2026-09-24)

A real log review found 804,597 parameter values whose unit the reader couldn't resolve. Root
cause: `RecordBuilder.BuildParamDef` wrote `Parameter.GetUnitTypeId()` (the parameter's UNIT, e.g.
`autodesk.unit.unit:millimeters`) into `pdef.spec`, not its SPEC (`autodesk.spec.aec:length-2.0.0`)
— `header.units` is keyed by spec, so it could never match. The same mix-up was in
`Pdra/ElementContextReader.InternalUnitLabel`, comparing `GetUnitTypeId()` against `SpecTypeId.*`
constants, which is never equal — silently disabling every internal-unit label. Both now read
`Parameter.Definition.GetDataType()` (the spec) instead.

`BuildDisplayUnits` also only ever populated `header.units` for 4 hard-coded specs
(length/area/volume/angle), so even a correct `spec` on any other parameter (temperature, cost,
speed, …) would still fail to resolve. It now fills the header from
`UnitUtils.GetAllMeasurableSpecs()`.

Fixing the writer alone would never repair an existing log: `pdef`/`cat` records are written once
per id and never re-checked, and `header` is only written at a segment start. `ModelLogService`
now starts a **new log generation** on a producer-version change (see "When the connector writes"
above) instead of the old forced-full-state reconcile, so an upgraded connector re-emits every
definition with the corrected `spec` — `ModelLogWriter.BeginNewGeneration` clears the pdef/cat
"seen" sets and forces an immediate (not threshold-gated) compaction so the clear survives a
crash before the next ordinary one. Producer version bumped to `0.6.0` so upgraded installs
trigger it.

## Round 8: UI-thread gzip, and a rotation that skipped full state (2026-09-24)

Two independent fixes, same review pass:

- `LogSegmentWriter.Rotate` gzipped the finished 64 MB segment synchronously (`CompressionLevel.
  Optimal`), inside an idle slice — a 1-2s UI freeze. Compression now runs on a background
  `Task`: the next segment opens immediately, the finished one is written to `.gz.tmp`, renamed
  to `.gz`, then the plain file is deleted, in that order (a reader always sees a complete plain
  file or a complete `.gz`, never a partial one of either). Crash/exit safety: the constructor
  redoes any leftover work (a rotated-away plain segment with no `.gz`, or a stray `.gz.tmp`);
  `Dispose` waits a bounded few seconds for an in-flight compression so an ordinary close usually
  leaves nothing to redo.
- A 64 MB rollover during an ordinary reconcile (`ModelLogWriter.RotateIfNeeded`, called at the
  start of `ReconcileJob`) rotated with only a `header` — no full state — breaking the rule that
  every segment can be read on its own. Fixed at the root by removing `RotateIfNeeded` entirely:
  `ModelLogWriter.RotationDue` now tells `ModelLogService` when the active segment is past
  threshold, and in that case it runs the same new-log-generation pass a producer-version change
  gets (`SnapshotJob`'s `isUpgrade` flag renamed to `newGeneration`, `_upgradeOwed` to
  `_newGenerationOwed`, since it's no longer upgrade-only) instead of a plain reconcile. A plain
  reconcile and live change capture never rotate now, so the active segment can run slightly over
  64 MB until the next open/sync/reconcile catches it.

## Round 9: incremental reconcile (2026-09-24)

Every reconcile (open with an existing log, and after every sync/reload) re-walked and re-hashed
every element just to find the handful that actually changed. Revit 2024+'s
`Document.GetChangedElements(Guid baseVersion)` names exactly what changed since a prior version,
so `ReconcileJob` now uses it when a trusted baseline exists (see "Incremental reconcile" above):
`ModelLogState.LastCompleteModelVersion` (persisted, journaled like the other scalar fields),
set only by a checkpoint that was both `complete` and caught the document unmodified
(`ModelLogWriter.WriteCheckpoint`'s new `documentUnmodified` parameter, from `!doc.IsModified`),
cleared by anything else and by `BeginNewGeneration`. The per-id write path is shared with live
change capture (`ModelLogService.WriteOneElement`, extracted from `ChangeCaptureJob`'s own loop,
which now also covers sheet/revision/material/link edits it previously left for the next full
reconcile) so there is exactly one "how to turn one Revit element into a record". Deletions are
matched by parsing the numeric ElementId out of a UniqueId's hex tail
(`src/ModelLog/UniqueIdElementId.cs`, unit-tested, including a 64-bit id and malformed input) and
looking it up against the hash cache's own known `el` ids — never asking Revit, which can no
longer resolve a deleted id to anything. `cp` records also gained `modelSaves`
(`DocumentVersion.NumberOfSaves`) alongside `modelVersion`, per the handoff's "version = GUID +
number".

## Round 10: deletions for every family (2026-09-24)

Deletion detection only ever compared the `el` family — a deleted type, level/room/space, grid,
material, sheet, revision or link stayed in the log forever. `ModelLogWriter.KnownElementIdsNotIn`
generalized to `KnownIdsNotIn(family, seenIds)`; `WriteDelete` gained a `family` parameter (default
`el`, unchanged behavior) that both selects which hash-cache family to remove from and, for every
OTHER family, adds the `del` record's new `of` field. `WalkModel` now collects a seen-id set per
family as it walks (node's is free — `nodeIdByLevelOrSpace`'s own values) and runs the same
deletion check for all eight at the end, in one shared `WriteFamilyDeletions` helper. The
incremental pass matches deleted ElementIds against node/type/mat directly (`"prefix" +
ElementId`, `ModelLogWriter.IsKnownId`) and el/grid/sheet/rev/link by their UniqueIds' numeric tail
(one `Dictionary<long,string>` per family, built once per batch).

## Round 11: measured CPU savings (2026-09-24)

Four changes, each verified with a before/after run of the same scratch benchmark
(`ModelLogWriter`/`LogSegmentWriter` directly, snapshot then reconcile-with-no-changes, n=33,600,
a frozen `git worktree` at the prior commit vs. this one):

| | Before | After |
|---|---:|---:|
| `snapshot` | 6.10s | 4.57s |
| `reconcile-nochange` | 4.41s | 3.65s |

- `RecordHash.Of` deep-cloned an entire canonicalized copy of every field-group just to call
  `ToJsonString()` on it. Rewritten to hash straight off a `Utf8JsonWriter` over a reused buffer
  — sorted keys, no clone at all (a leaf value's own `WriteTo` produces identical bytes to
  cloning-then-serializing, since cloning never changes formatting) — verified byte-identical to
  the old implementation across nested/array/unicode/special-float inputs
  (`RecordHashCompatTests`, which keeps the old implementation only for that comparison).
- `ModelLogWriter.Append`/`WithId` deep-cloned every field again when moving it into the appended
  line. Every caller builds its `JsonObject` fresh and never reads it again afterward, so this now
  MOVES each field (remove from the source, then assign — satisfies `JsonNode`'s single-parent
  rule without a clone) via a shared `MoveFieldsInto` helper — a real win for nested subtrees (a
  `p` object's 40 parameters, a `bb`/`mats` array) that used to be recursively duplicated.
- `LogSegmentWriter.AppendLine` flushed after every single line. Flushing now happens once per
  idle slice (`ModelLogWriter.FlushJournalBuffer`, called by `FlushState` — the log first, then
  the journal, keeping crash-safety rule #2), and before anything that persists state depending on
  it (a checkpoint, session record, rotation, Dispose). `CurrentSizeBytes` flushes first so a
  rotation decision is never based on a stale, not-yet-flushed size.
- `ModelLogService.RefreshIndexCache` re-walked the whole document (node index, grid lines, tag
  index) right after a full `WalkModel` pass had already built the same structures. `WalkModel`
  now fills a caller-supplied `IndexCache` in place as it walks; the caller commits it directly
  (only once its own staleness check confirms the pass actually finished) instead of triggering a
  second walk. An incremental reconcile still calls the old `RefreshIndexCache` — it only merges
  in what changed, so a real rebuild is what purges anything deleted.

## Round 12: missing spec fields (2026-09-24)

- Header `coordinates` gained `sharedTransform` (`ActiveProjectLocation.GetTotalTransform()`) —
  the doc comment already promised it; the code never computed it.
- `rel.hosted`: the reverse of `rel.host` (ids of elements THIS one hosts), via a reverse index
  (`RecordBuilder.BuildHostedIndex`) built once per pass, the same shape as the tag-to-sheet index.
  `rel.link` doesn't apply here: this log only ever walks the host document's own elements, never
  a linked document's, so there is no "belongs to a link" membership for an element to carry — a
  link is already its own `link` record kind.
- `q.thickness` for walls/floors/roofs/ceilings — one more entry in the existing by-name BIP
  resolution list (`WALL_ATTR_WIDTH_PARAM`/`FLOOR_ATTR_THICKNESS_PARAM`/
  `ROOF_ATTR_THICKNESS_PARAM`/`CEILING_THICKNESS`), same as every other quantity.
- `sheet.elements` (tagged or dimensioned elements) and `rev.sheets`/`rev.clouds` — three more
  once-per-pass reverse indexes (`BuildSheetElementIndex`, `BuildRevisionSheetIndex`,
  `BuildRevisionCloudIndex`), the tagged half of `sheet.elements` reusing the existing tag index.
  Capped at 500 elements per sheet. A live/incremental edit to just one sheet or revision reuses
  its existing value for these fields rather than rebuild a whole-document index for one edit —
  the next full pass fills them in.
- Segment retention: `ConnectorSettings.ModelLogRetentionDays` (default 90) — see "Retention"
  above.

## Round 13: v0.6.0 real-model review (2026-09-25)

Session of 25 Sep on `PDR_Horizons_BWK_R25` (v0.6.0): header + session + full snapshot + `cp
complete: true` (189,136 elements in the model, 42,923 `el` records, written 09:41–09:47); `eid`
100%, `loc` 95%, `type` 83%, `pdef` spec 93%, `mats` 63% (504 `mat`), 322 `sheet` records. Producer
version bumped to `0.6.1`, so the next open starts a new log generation that applies all of this
to the existing log (the lines/detail items already logged get `del` with `reason: "filtered"`).

| # | Finding | Cause | Fix |
|---|---|---|---|
| 1 | Noise filter not working: 6,275 Lines and 5,116 Detail Items among 42,923 `el` (~27%) | Revit files `OST_Lines` (model + detail lines) and `OST_DetailComponents` under `CategoryType.Model`, and the type checks didn't cover curve elements or view-owned elements | `RecordBuilder.IsLoggableModelElement` now also excludes view-specific elements (`Element.ViewSpecific`), every `CurveElement`, element types, reference planes, and a `BuiltInCategory` list (lines, detail items, detail groups, separation/boundary/sketch/insulation lines, sun path, cameras, legend components, work plane grid, raster images, match lines, reference planes), resolved by name so a name missing from one API target can't break the build |
| 2 | No closing `cp`: yesterday's segment has no `cp` at all; `state.json` says `LastCheckpointClosed: false` | Not provable from the log alone (no Revit here). Found in code, each able to produce exactly that: (a) every sync ABANDONED the running snapshot/reconcile and restarted it from scratch, so on a model synced more often than one pass takes (~6 min here) no pass ever reached its `cp`; (b) any exception escaping a pass dropped the job silently — no `cp`, and live change capture held back for the rest of the session; (c) `state.json` is only a base file: `LastCheckpointClosed` lives in its journal (`state.<gen>.jsonl`) until the next compaction, so the base can read `false` after a clean close; (d) the closing `cp` ran after a slow ModelFacts lookup, and nothing wrote one for a model still open at add-in shutdown | (a) a sync pauses and resumes the pass instead (sync epoch; `FinishWalk` writes `complete: false` and queues a follow-up reconcile); (b) per-record guard (`TryRun`, counted in `cp.errors`) plus a job-level guard that writes a `gap` and releases change capture; (c) a `closed: true` checkpoint compacts `state.json`; (d) the closing `cp` is written first, and `ModelLogService.CloseAll` runs from `OnShutdown` |
| 3 | 21,836 `del` at the start of a new segment — cleanup or real? | Deletion detection on a new generation compares the hash cache with a fresh walk; it can't be told from the records which it was. Candidates in code: records an earlier version logged that this one doesn't (pre-filter noise, types no logged element uses), modified element TYPES that change capture had logged as `el` (then "deleted" by every full walk), areas logged as `node` by change capture but never by the walk, plus real deletions not yet reconciled from a session that never reached its `cp` | Every `del` now carries `reason` (`deleted`/`filtered`/`unreferenced`), decided by asking the model whether the element still exists. Change capture no longer logs element types as `el` (they update their `type` record) or areas as `node` |
| 4a | `rel.roomFrom/To` 4% | Only the document's last phase was tried; rooms exist per phase | Doors/windows: also the instance's created phase, then every other phase; then a geometric fallback (`rel.roomSource: "geometric"`) |
| 4b | `h.mark` 12% | Most likely the model's own data: `h.mark` is `ALL_MODEL_MARK`, present only when Mark is filled in — never invented | No code change. To verify: count `el` records whose `p` has a non-empty `builtin:ALL_MODEL_MARK`; it should equal the `h.mark` count |
| 4c | `el.sheets` 4% with 322 sheets | `sheets` counted only TAGGED elements | Now also every sheet whose placed model views show the element (per-view visibility, one view per idle step) |
| 5 | ~315k records, live segment 72 MB+ in one heavy session | No rotation outside open/sync; every sync-restarted snapshot re-wrote the full state; the noise of #1; `p` rewritten whenever "Edited by" (the worksharing borrower) changed | Size-based continuation rotation (see "Rotation"); passes resume instead of restarting; #1's filter; `EDITED_BY` excluded from `p` like any other value that changes on its own |

**Still to verify in Revit** (not possible in this sandbox): the per-view visibility pass's cost on
the largest views (3D views especially) against the 50 ms slice budget; that `DocumentClosing` is
raised for every open model when Revit itself exits (`CloseAll` covers it if not); the counts above
after one session on v0.6.1 (`el` without lines/detail items, `del` reasons, `roomFrom/To`, `sheets`,
segment sizes).

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
9. **Sync safety:** start a Synchronize with Central while a reconcile is visibly in progress
   (right after opening a large model). Revit must not crash. The log must show no records
   between the sync starting and finishing; then the same pass resumes (no second `header`/full
   state), ends in a `cp` with `complete: false`, and a follow-up reconcile ends in a `cp` with
   `complete: true`. Repeat with
   Reload Latest and with closing the model mid-reconcile (its closing `cp` should read
   `complete: false`).
10. **Post-event pairing:** confirm Revit raises `DocumentSynchronizedWithCentral`,
   `DocumentReloadedLatest` and `DocumentSaved` even when the operation fails or is cancelled
   (e.g. cancel a sync). If one is skipped, model logging stays paused until that document closes.
   That's safe but silent, so the pause would need a fallback.

**Done** = all seven steps merged; the two recorded fixtures checked in; the ten Revit checks
passed and written up; the add-in rebuilt and installed on the office machines. This repo has
merged all seven steps' code; the fixtures and the ten Revit checks are the open item.
