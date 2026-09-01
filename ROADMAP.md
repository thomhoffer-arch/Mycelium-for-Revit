# Mycelium Studio — Revit Connector Roadmap

A Revit **model source** for Mycelium Studio. Exposes Revit data over MCP tools that satisfy the Revit model-source contract (`docs/CONTRACT.md`). It is interchangeable with PDRA — two implementations, one contract.

> **Role boundary (never crosses this line).** The connector exposes *raw Revit data via MCP tools*.
> It does **not** build spine records, run a provenance ledger, or carry triage/compliance logic —
> **Mycelium Studio** does all of that. Every item below keeps the connector a thin translator.

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
