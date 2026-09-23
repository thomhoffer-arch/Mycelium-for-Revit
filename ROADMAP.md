# Mycelium Studio — Revit Connector Roadmap

A Revit **model source** for Mycelium Studio. Writes the model as an append-only change log
(`docs/MODEL_LOG.md` — the current plan and primary spec) and serves a small on-demand/acting MCP
tool set alongside it. The connector's original per-tool MCP read interface, below, is now
**frozen** — see docs/MODEL_LOG.md and the "Model log rewrite" section just below.

> **Role boundary (never crosses this line).** The connector exposes *raw Revit data* (the log,
> plus on-demand detail). It does **not** build spine records, run a provenance ledger, or carry
> triage/compliance logic — **Mycelium Studio** does all of that.

## Model log rewrite (in progress) — see docs/MODEL_LOG.md

Stop answering read requests; write the model as an append-only, checkpointed JSON Lines change
log instead. Loam reads the log; nobody calls anyone.

- [x] **1. Security fix** — settings file (`src/RevitBridge/ConnectorSettings.cs`), auto-generated
      token, server refuses to start without one. Replaces the `LOAM_REVIT_*`/`MYCELIUM_REVIT_*`
      env-var mismatch that silently ran with no bearer auth.
- [x] **2. Log writer** — `src/ModelLog/`: format, segments, gzip on rotate, `state.json`, writer
      lock, crash safety. Its own Revit-free class library, unit-tested (`tests/ModelLog.Tests/`,
      CI via `.github/workflows/modellog-tests.yml`).
- [x] **3. Definitions + snapshot on open**, in ≤50ms idle slices (`ModelLogService.SnapshotJob`,
      `IdleSliceRunner`).
- [x] **4. Change capture** — `DocumentChanged` queue → idle processing, partial states, `chg`
      records (`ModelLogService.ChangeCaptureJob`).
- [x] **5. Reconcile** on open and after sync/reload — self-healing, catches other users' changes
      (`ModelLogService.ReconcileJob`).
- [x] **6. Full richness** — handles, grid refs, relations, materials with quantities, sheets,
      revisions, all parameters (`ModelLogCapture/RecordBuilder.cs`). **Unverified** — several
      heuristics (per-category quantity parameters, the grid-intersection label) are marked
      `NEEDS LIVE-REVIT CHECK` in that file; no Revit available in the sandbox that wrote this.
- [x] **7. Tools** — `get_element_detail`, `find_elements`, `show_element`, `isolate_elements`,
      `open_sheet`; the read tools below are now frozen (kept for compatibility, no new capability,
      removed once the log is in active use).

**Not done — needs an actual Revit session (tracked, not forgotten):**
- [ ] Record two real fixture logs (one workshared, one with MEP) and check them in as test data.
- [ ] The six-item live-Revit verification checklist in docs/MODEL_LOG.md's "Order, verification
      and done" section (idle-slice timing, lossless round-trip, gap/crash recovery, actual size
      vs. the estimate table, `DocumentChanged`-after-sync behavior, the quantity/grid heuristics).
- [ ] `dotnet build` of `LoamRevitConnector.csproj` for both `net48`/`net8.0-windows` — not run
      locally (no .NET SDK in the sandbox that wrote this); only `tests/ModelLog.Tests/` (Revit-free)
      has been build/test-verified, via CI.

## Status — done (v0.3)

- [x] **Spatial-attribute pass (Workstream B, v0.3):** `type_id`/`mark`/`design_option`/`from_link`/`room`
      promoted onto `list_elements` (and `type_id`/`mark`/`room` onto `filter_elements_by_scope_box`) from
      the sibling tools that already proved them reachable; a generic `params[]` request arg plus the new
      `get_element_parameters` discovery tool for any parameter, typed with unit metadata instead of a bare
      display string; `offset`/`next_offset` paging on `list_elements` with a stable ElementId-ascending
      order; the `DocumentChanged` push now splits added/modified, and sends deletions, transaction
      name(s), and who last changed it (workshared models only). See `docs/CONTRACT.md`'s changelog for the
      full field-by-field list, and this document's own "Fixed" section below for the doc-reconciliation
      pass that came with it (six field-name drifts between this document and the actual code). The plan's
      full lean-IFC-shaped spatial tree (relations/quantities beyond a single `room` attachment) is a
      larger, separate effort, deliberately out of scope for this pass.

## Status — done (v0.2)

- [x] `get_model_revision`
- [x] `get_project_info` *(optional — `Document.ProjectInformation` → enables auto-seed)*
- [x] `list_elements` *(general enumeration — no scope box, no id, no category required; unscoped it walks the
      whole document bounded by `limit`. The primitive Loam's connector-agnostic layer looks for by name
      pattern (`list_/find_/enumerate_elements`) — without it, this connector could never be sampled for
      `list_elements`, only used by-id, so Loam's conformance probe correctly reported "no element-LIST tool
      to enumerate rows" even when the connector was fully reachable.)*
- [x] `filter_elements_by_scope_box`
- [x] `get_element_by_uniqueid`
- [x] `get_element_by_ifcguid`
- [x] `get_door_rooms`
- [x] `get_classification_sources` *(discovery for "which parameter IS classification here" — samples the
      model and reports candidate parameters with a populated count and sample values, rather than picking
      a winner; see "Fixed" below)*
- [x] `get_rooms`
- [x] `get_levels`
- [x] `get_views`
- [x] `get_sheets`
- [x] `get_links`
- [x] MCP-over-HTTP server (initialize / tools/list / tools/call), bearer auth, `content[0].text` JSON framing
- [x] Multi-targeted: `net48` (Revit 2024) and `net8.0-windows` (Revit 2025/2026)
- [x] Silent multi-instance load (second Revit skips port, loads without error)
- [x] One-click `install.bat` — auto-detects Revit versions, registers MCP in Claude Desktop and Claude Code
- [x] Event push — fire-and-forget `POST /api/model-event` on Revit document open/save/change/close (debounced), so the orchestrator doesn't poll. Additive; silent no-op when the orchestrator is down.

## Near-term — polish (no contract change)

- [x] **Self-test script** — `tools/selftest.ps1` drives every tool against a live model over the real MCP
      endpoint and checks field names/shapes against `docs/CONTRACT.md`, including a bulk-vs-by-ID
      classification equality check (`list_elements` vs `get_element_by_uniqueid`/`get_element_by_ifcguid`
      for the same element). Can't run in CI — needs Revit + an open model — so it's a manual/scheduled
      check, not a build gate.

## Fixed

- [x] **`docs/CONTRACT.md` had drifted from the code in six places, plus an env-var name drift in
      `README.md`** — a sibling repo's plan doc (Loam PR #669) found this document showed `get_rooms`
      returning flat `level_name`/`area_sqft`/`area_display` (the code has always nested `level{}` and
      used `area_sf`/`area_user_units`), `get_levels` showing `elevation`/`elevation_display` (code:
      `elevation_ft`/`elevation_user_units`), `get_links` showing `is_loaded` (code: `loaded`), `get_sheets`
      documenting a numeric `id` and a `name` field neither ever emitted (the real fields are
      `unique_id`/`sheet_number`/`sheet_name`), and `get_door_rooms`' `door_params` values shown as an
      unquoted number when `ElementContextReader.ReadParamValue` has always returned a string. Separately,
      `README.md`'s Transport table (and this document's own Transport section) documented
      `MYCELIUM_REVIT_LISTEN`/`MYCELIUM_REVIT_URL`/`MYCELIUM_REVIT_TOKEN`, while `src/App.cs` has always
      read `LOAM_REVIT_LISTEN`/`LOAM_REVIT_TOKEN` — so setting the documented env vars silently left bearer
      auth off. All six fixed directly in `docs/CONTRACT.md` (now v0.3) and `README.md`; no code changed
      for this item — the code was already correct, the document was wrong.
- [x] **`unique_id`/`ifc_guid` carried no document-instance guard — a copied/Save-As/split RVT could
      produce a false "same project" merge downstream** — an external analysis (Loam) found two
      genuinely different projects (numbered 2033 and 2322) whose Revit UniqueIds and IFC GUIDs
      overlapped ~84-93%, because one model had been copied/Save-As'd/split from the other. Both
      Autodesk's own docs (UniqueId is unique "within the document") and the Revit-IFC team
      ([autodesk/revit-ifc#378](https://github.com/Autodesk/revit-ifc/issues/378) — copying+exporting
      an RVT trivially produces duplicate IFC GlobalIds, "expected for copied or split models", no
      built-in "reset document GUIDs") confirm this is expected Revit behaviour, not a connector bug —
      but every element-returning tool here (`get_sheets`, `list_elements`,
      `get_element_by_uniqueid`, `get_element_by_ifcguid`, `filter_elements_by_scope_box`,
      `get_door_rooms`) returned `unique_id`/`ifc_guid` with nothing to tell two document instances
      apart, so a caller joining on those alone had no way to know a match might just mean "shares
      lineage", not "is the same project". Fixed with `model_instance_id` (camelCase
      `modelInstanceId` alongside the existing `source`/`sourceLocalId`/`projectKey` spine keys on
      `get_element_by_uniqueid`/`get_element_by_ifcguid`; snake_case `model_instance_id` elsewhere,
      matching each tool's own existing field-naming convention) — one new `ModelFacts.ModelInstanceId`
      property (the cloud project+model GUID pair when known, else the central model path, else
      null) stamped onto every element row and every relevant response envelope, and a linked
      element gets the LINK's own identity, never the host's. Deliberately does **not** fabricate an
      identity for a genuinely standalone, non-workshared, non-cloud document (including a fresh
      Save As/copy) — Revit's API exposes no cross-copy identity for that case, so `model_instance_id`
      stays absent there and callers are told, in the field's own contract, to treat absence as
      "cannot rule out a collision", never as a value safe to join on. See `docs/CONTRACT.md`'s
      "Identity rules" section for the full contract.
- [x] **`filter_elements_by_scope_box` and `get_door_rooms` were silently non-joinable** — `docs/CONTRACT.md`
      always documented `unique_id`/`ifc_guid` on both tools' rows (the connective-spine identity keys), but
      neither tool ever actually set them — a doc/implementation drift that meant the ONE tool that enumerates
      elements (before `list_elements` existed) returned rows with nothing a caller could join on. Fixed by
      setting both fields from the same `Element.UniqueId` / `IFC_GUID` parameter every other identity-bearing
      tool already reads.
- [x] **Classification was unreachable for any non-Uniformat/OmniClass scheme, and unevenly wired across
      tools** — an external analysis (Loam, on a Dutch NLRS model) reported 0 of 2,213 elements classified
      and read that as "no classification field at all." The field existed and was correctly omitting empty
      values, not missing — the real gap was that `ElementContextReader.ResolveClassification` only ever
      probed Revit's own Assembly Code / OmniClass built-ins, which Dutch (NL-SfB) and UK (Uniclass) offices
      never populate; their classification lives in an office-named shared/project parameter the connector
      had no way to reach or even name. Fixed with `classification_params` (probe a caller-named parameter,
      type then instance, on every element-returning tool), `get_classification_sources` (discover the name
      instead of guessing it), and a `classification_sources` response envelope that reports what was probed
      and how many returned rows had it populated — so "field unsupported" and "no element has a value" stay
      distinguishable without ever writing `classification: null` onto a row (kept to the omit-never-blank
      rule; see `docs/CONTRACT.md`'s Classification section for the full contract). Also closed while in
      there: `get_element_by_ifcguid` was documented as returning "the same element shape" as
      `get_element_by_uniqueid` but never carried `level`/`classification` — now it does; and
      `filter_elements_by_scope_box` was writing `"level": null` / `"design_option": null` instead of
      omitting them, the same blanking bug the entry above fixed for `unique_id`/`ifc_guid` on this tool.
- [x] **`get_classification_sources` couldn't discover an instance-level classification parameter, and
      `category` round-tripped nowhere** — a second round of the same Loam/NLRS feedback: with `all: true`,
      discovery only ever reported type-level string parameters (instance parameters skipped outright), so
      an office classification value set per instance (common for NLRS) was invisible no matter what
      argument was passed. Separately, every element-returning tool's `category` field was the Revit
      display name ("Walls"), while every tool's `category` *argument* parsed the BuiltInCategory enum name
      ("OST_Walls") via `Enum.TryParse` — so a category value read off one tool's row and fed back into
      another tool's `category` arg always failed with "Unknown BuiltInCategory". Fixed with a `scope`
      argument (`heuristic` default / `type` / `instance` / `all` — `type` is the legacy `all: true`,
      unchanged; `instance` and `all` scan name- and storage-agnostically, including instance parameters),
      a `category_id` field alongside `category` on every row, and category-argument resolution that tries
      the enum name first and falls back to the document's own category display names — so a caller never
      needs a Revit-specific category table. Also while in there: unscoped discovery's sample is now spread
      across the categories present rather than taken in raw document order, which previously biased every
      unscoped call toward whichever categories happen to sort first.

## Robustness

- [ ] **Batching / performance** — `get_element_by_ifcguid` scans all elements per call; cache the `IFC_GUID → element` index per document revision for large models.
- [ ] **Linked-model elements** stay flagged `from_link: true` and are never silently merged into the host set. Keep that guarantee.
- [ ] **Error semantics** — structured JSON error payload (not just HTTP 500) so clients can surface a clean per-tool warning.

## Write-back — FUTURE (additive, gated)

When Mycelium Studio wires Revit write-back, the connector gains the matching write primitives — **additively** (contract semver: additive → minor bump):

- [ ] `edit_element` — set parameter(s) on an element. Returns the new state. Mycelium Studio owns propose → human-approve → ledger; the connector performs the approved op.
- [ ] `create_workitem` — create the Revit-side artefact for a coordination action.
- [ ] **Reversible / transactional** — named transaction group so a change can be rolled back; surface a transaction id for the ledger.

Do **not** add write tools until Mycelium Studio calls them.

## Non-goals (keep these in Mycelium Studio, never here)

- Spine records (identity / freshness / provenance) — **Mycelium Studio** constructs them from these fields.
- The provenance ledger — **Mycelium Studio** owns it.
- Triage, compliance verdicts, profiles' rule logic — **Mycelium Studio**.
- Any classification crosswalk / accumulated judgment — that's Mycelium Studio's private moat.
