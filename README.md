# loam-revit-connector — Revit Model Source for the Connective Spine

This repository will implement a **model source** for Autodesk Revit, exposing Revit data via **MCP tools** to satisfy the [Connective Spine contract](https://github.com/thomhoffer-arch/Mycelium) (canonical in Mycelium).

---

## Status: Early Stage
- **1 commit**: Contract defined, implementation pending.
- **Target**: Expose the following 5 primitives (aligned with PDRA):
  1. `getModelRevision` — Returns the current Revit model revision.
  2. `filterElementsByScopeBox` — Filters Revit elements by a scope box.
  3. `getElementsByUniqueId` — Retrieves elements by Revit UniqueId.
  4. `getElementsByIfcGuid` — Retrieves elements by IFC GUID.
  5. `getDoorRooms` — Retrieves door-room relationships.

---

## How It Works
- **Does not emit spine records**: This connector is a **model source**. It exposes raw Revit data via MCP tools.
- **Spine records are constructed by Loam**: The [Loam orchestrator](https://github.com/thomhoffer-arch/Loam) consumes these tools, constructs **spine records** (identity + freshness + provenance event), and maintains the **provenance ledger**.

---

## Compatibility
- **Spine Version**: `v0.1` (see [Mycelium](https://github.com/thomhoffer-arch/Mycelium)).
- **Orchestrator**: Designed for [Loam](https://github.com/thomhoffer-arch/Loam).

---

## See Also
- [Mycelium (Connective Spine)](https://github.com/thomhoffer-arch/Mycelium)
- [Loam (Orchestrator)](https://github.com/thomhoffer-arch/Loam)
- [PDRA (Revit MCP Tools)](https://github.com/thomhoffer-arch/PDRA)