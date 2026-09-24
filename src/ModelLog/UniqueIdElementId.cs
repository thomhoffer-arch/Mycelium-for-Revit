using System;
using System.Globalization;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// Recovers the numeric ElementId encoded in the tail of a Revit <c>Element.UniqueId</c> —
    /// the hex characters after its LAST '-' (Revit's own documented shape: a GUID followed by a
    /// hyphen and the element's id in hex). Used to match a DELETED ElementId — <c>
    /// Document.GetChangedElements</c>'s <c>DocumentDifference.GetDeletedElementIds()</c> only
    /// ever gives numbers, never resolvable to a live Element or its UniqueId any more — against
    /// the UniqueIds a model log's hash cache already knows, without asking Revit for anything.
    /// See <c>ModelLogCapture.ModelLogService</c>'s incremental reconcile.
    ///
    /// NEEDS LIVE-REVIT CHECK: this assumes the tail is always exactly the element's own
    /// ElementId in hex for a plain (non-linked, non-copied) element — true for the ordinary
    /// case this is used for (matching a deletion inside the SAME session's own document), but
    /// not the same recovery <see cref="IfcGuid"/> does for a cross-copy IFC GlobalId (that one
    /// additionally XORs against a GUID segment, a different Revit quirk for linked/copied
    /// elements). A tail that doesn't parse — wrong shape, non-hex, too long for a 64-bit id
    /// (Revit 2024+ widened ElementId to 64 bits) — is simply skipped, never guessed.
    /// </summary>
    public static class UniqueIdElementId
    {
        /// <summary>Parses the ElementId encoded in <paramref name="uniqueId"/>'s tail (the hex
        /// after its last '-'). False — <paramref name="elementId"/> left at 0 — for anything not
        /// cleanly shaped that way: no '-', an empty or over-long (&gt;16 hex chars) tail, or a
        /// tail containing a non-hex character.</summary>
        public static bool TryGetElementIdTail(string? uniqueId, out long elementId)
        {
            elementId = 0;
            if (string.IsNullOrEmpty(uniqueId)) return false;

            var dash = uniqueId!.LastIndexOf('-');
            if (dash < 0 || dash == uniqueId.Length - 1) return false;

            var hex = uniqueId.Substring(dash + 1);
            if (hex.Length == 0 || hex.Length > 16) return false;
            foreach (var c in hex)
                if (!Uri.IsHexDigit(c)) return false;

            return long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out elementId);
        }
    }
}
