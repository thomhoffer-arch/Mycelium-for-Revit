namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// The <c>k</c> values from docs/MODEL_LOG.md's record-kind table. One string constant
    /// per kind so every call site (writer, capture, tests) spells a kind the same way —
    /// see docs/MODEL_LOG.md for what each kind carries and when it's written.
    /// </summary>
    public static class RecordKinds
    {
        public const string Header = "header";
        public const string Project = "project";
        public const string Pdef = "pdef";
        public const string Cat = "cat";
        public const string Node = "node";
        public const string Grid = "grid";
        public const string Mat = "mat";
        public const string Type = "type";
        public const string El = "el";
        public const string Del = "del";
        public const string Sheet = "sheet";
        public const string Rev = "rev";
        public const string Link = "link";
        public const string Chg = "chg";
        public const string Checkpoint = "cp";
        public const string Gap = "gap";

        /// <summary>Kinds written once, the first time an id is seen, and never re-checked
        /// (definitions) — as opposed to every other non-append-only kind, which goes through
        /// <see cref="ModelLogWriter.WriteIfChanged"/>'s hash-cache change detection.</summary>
        public static bool IsWriteOnce(string kind) => kind == Pdef || kind == Cat;
    }
}
