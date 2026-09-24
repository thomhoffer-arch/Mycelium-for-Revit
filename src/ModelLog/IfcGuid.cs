using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// Revit <c>Element.UniqueId</c> → IFC GlobalId conversion — ported from SRM's
    /// <c>srm/ifcguid.py</c> (thomhoffer-arch/SRM), verified byte-for-byte there against
    /// <c>ifcopenshell.guid.compress</c>/<c>expand</c>. Pure string/byte math, no Revit API, so
    /// no net48-only-API concern and no dependency beyond this file.
    ///
    /// WHY THIS EXISTS (round 3 of the handoff plan): ClashControl, BCF issues, IFC exports and
    /// emails all cite the IFC GlobalId, never Revit's UniqueId — the identity role already names
    /// "IFC GUID parameter when present" as an element field; this fills it in for every element,
    /// not only the ones a caller happened to export through Revit's own IFC exporter with "Store
    /// IFC GUID" enabled. This is emitting an extra identifier, never converting the model to
    /// IFC — <c>src/ModelLogCapture/RecordBuilder.cs</c> still does no IFC export or geometry
    /// conversion of any kind.
    ///
    /// Revit's <c>Element.UniqueId</c> is a 45-character string: a standard 36-char GUID followed
    /// by a hyphen and an 8-hex-digit "episode counter" (an artifact of how Revit stamps copied/
    /// centrally-linked elements). The documented conversion to an IFC GlobalId:
    ///
    /// 1. XOR the GUID's last 8 hex characters (its last 4 bytes) with the episode counter,
    ///    recovering the underlying 128-bit id Revit's own IFC exporter uses.
    /// 2. Compress that 128-bit value into IFC's 22-character GlobalId per buildingSMART's
    ///    documented scheme: pad to 18 bytes, standard-base64-encode (→ 24 chars), drop the
    ///    first 2 chars (they only ever encode the padding), then translate from the standard
    ///    base64 alphabet into IFC's own digit/upper/lower/<c>_</c>/<c>$</c> alphabet.
    /// </summary>
    public static class IfcGuid
    {
        private const string IfcAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
        private const string StdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        private static readonly Regex UniqueIdPattern = new Regex(
            @"^([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})-([0-9a-fA-F]{8})$",
            RegexOptions.Compiled);

        /// <summary>32 hex chars (16 bytes / 128 bits, dashes allowed) → 22-char IFC GlobalId.</summary>
        public static string Compress(string hex32)
        {
            var cleaned = new StringBuilder(32);
            foreach (var c in hex32)
                if (Uri.IsHexDigit(c)) cleaned.Append(char.ToLowerInvariant(c));
            if (cleaned.Length != 32)
                throw new ArgumentException($"expected 32 hex characters, got {cleaned.Length}: {hex32}");

            var padded = "0000" + cleaned; // 36 hex chars = 18 bytes = 144 bits = 24 base64 chars, no remainder
            var bytes = HexToBytes(padded);
            var std = Convert.ToBase64String(bytes).Substring(2); // first 2 chars only ever encode the padding
            return Translate(std, StdAlphabet, IfcAlphabet);
        }

        /// <summary>22-char IFC GlobalId → 32 hex chars (inverse of <see cref="Compress"/>).</summary>
        public static string Expand(string ifcGuid)
        {
            if (ifcGuid.Length != 22)
                throw new ArgumentException($"expected a 22-character IFC GlobalId, got {ifcGuid.Length}: {ifcGuid}");
            var std = Translate(ifcGuid, IfcAlphabet, StdAlphabet);
            var bytes = Convert.FromBase64String("AA" + std);
            return BytesToHex(bytes).Substring(4);
        }

        /// <summary>Revit <c>Element.UniqueId</c> (45 chars) → IFC GlobalId (22 chars). Returns
        /// null — never throws — when <paramref name="uniqueId"/> isn't shaped like
        /// <c>&lt;8-4-4-4-12 hex guid&gt;-&lt;8 hex episode counter&gt;</c>, matching this
        /// codebase's "omit, don't fabricate" rule for anything not cleanly resolvable.</summary>
        public static string? FromRevitUniqueId(string uniqueId)
        {
            var m = UniqueIdPattern.Match(uniqueId.Trim());
            if (!m.Success) return null;

            var guidHex = m.Groups[1].Value.Replace("-", "").ToLowerInvariant();
            var episodeHex = m.Groups[2].Value.ToLowerInvariant();

            uint tail, episode;
            try
            {
                tail = Convert.ToUInt32(guidHex.Substring(24, 8), 16);
                episode = Convert.ToUInt32(episodeHex, 16);
            }
            catch { return null; }

            var xoredTail = (tail ^ episode).ToString("x8");
            return Compress(guidHex.Substring(0, 24) + xoredTail);
        }

        private static string Translate(string input, string fromAlphabet, string toAlphabet)
        {
            var sb = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                var idx = fromAlphabet.IndexOf(c);
                sb.Append(idx >= 0 ? toAlphabet[idx] : c);
            }
            return sb.ToString();
        }

        // Convert.FromHexString/ToHexString are .NET 5+ only — this library also targets net48
        // (Revit 2024), so hex (de)coding is hand-rolled here, matching this repo's existing
        // net48-compat pattern (see src/Pdra/SpineKeys.cs's own manual hex encoding).
        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
