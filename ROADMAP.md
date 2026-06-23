# Mycelium Studio — Revit Connector Roadmap

A Revit **model source** for Mycelium Studio. Exposes Revit data over MCP tools that satisfy the Revit model-source contract (`docs/CONTRACT.md`). It is interchangeable with PDRA — two implementations, one contract.

> **Role boundary (never crosses this line).** The connector exposes *raw Revit data via MCP tools*.
> It does **not** build spine records, run a provenance ledger, or carry triage/compliance logic —
> **Mycelium Studio** does all of that. Every item below keeps the connector a thin translator.

## Status — done (v0.2)

- [x] `get_model_revision`
- [x] `get_project_info` *(optional — `Document.ProjectInformation` → enables auto-seed)*
- [x] `filter_elements_by_scope_box`
- [x] `get_element_by_uniqueid`
- [x] `get_element_by_ifcguid`
- [x] `get_door_rooms`
- [x] `get_rooms`
- [x] `get_levels`
- [x] `get_views`
- [x] `get_sheets`
- [x] `get_links`
- [x] MCP-over-HTTP server (initialize / tools/list / tools/call), bearer auth, `content[0].text` JSON framing
- [x] Multi-targeted: `net48` (Revit 2024) and `net8.0-windows` (Revit 2025/2026)
- [x] Silent multi-instance load (second Revit skips port, loads without error)
- [x] One-click `install.bat` — auto-detects Revit versions, registers MCP in Claude Desktop and Claude Code

## Near-term — polish (no contract change)

- [ ] **Self-test script** — call each tool against a sample model and check the response shape against `docs/CONTRACT.md`, so field-name drift fails loudly.

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
