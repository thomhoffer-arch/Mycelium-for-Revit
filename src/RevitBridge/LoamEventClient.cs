using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Loam.Revit.Connector.RevitBridge
{
    /// <summary>
    /// Fire-and-forget push of Revit document events to Loam's local model-event
    /// endpoint (<c>POST http://127.0.0.1:{LOAM_HTTP_PORT|47600}/api/model-event</c>).
    ///
    /// This is purely additive: if Loam isn't running the connection refuses and we
    /// swallow it silently — Revit never blocks and never sees a dialog. Loam falls
    /// back to polling when these events are absent.
    ///
    /// Constraints honoured here:
    ///  • Loopback only (127.0.0.1) — Loam rejects non-loopback writes by default.
    ///  • No Origin / Sec-Fetch-* headers — a plain HttpClient sends none; Loam treats
    ///    any browser-style header as hostile, so we never add them.
    ///  • Short timeout, background thread, all errors swallowed.
    ///  • DocumentChanged is throttled to at most one POST per window (it fires on
    ///    every transaction); opened/saved/closed are sent immediately.
    /// </summary>
    public sealed class LoamEventClient : IDisposable
    {
        // Single shared client, short timeout. Static so we never exhaust sockets.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        private static readonly TimeSpan ChangedWindow = TimeSpan.FromSeconds(45);

        // ENERGY EFFICIENCY — "Loam should always only ask for new/changed things" (live request). A
        // DocumentChanged transaction's touched UniqueIds ride along with the "changed" push so Loam can
        // resolve ONLY those elements (pdra_get_element_by_uniqueid — a direct per-id lookup) instead of
        // re-walking every sheet/view via get_sheets(include_elements) on its next read. Accumulated (a
        // HashSet, unioned) across the WHOLE debounce window, not just the latest transaction — several
        // small edits within one window must all be covered. Bounded: past MaxChangedIds a transaction
        // touched more elements than a batch lookup is worth (an IFC reload, a whole-model import) — the
        // list is dropped entirely (never silently truncated) so Loam falls back to its own bounded full
        // sweep instead of acting on a partial, misleadingly-complete-looking id list.
        //
        // DISRUPTION (live report: "a whole list of views generating graphics" interrupting normal
        // Revit use) — this used to be caused by Loam's full-sweep fallback calling
        // get_sheets(include_elements=true) unscoped, which forced Revit to regenerate graphics for
        // every view in the document. That path is now closed at the source: GetSheetsTool rejects
        // include_elements=true without sheet_number, so it can no longer touch more than one sheet's
        // views per call (see GetSheetsTool.Run's ROOT FIX note). Raising MaxChangedIds here is a
        // separate, complementary change — 300 was cheaply crossed by an everyday batch edit (e.g.
        // Purge Unused on a few hundred elements), pushing Loam into a full re-sync more often than a
        // genuinely-too-big transaction warrants. Raised so routine batch edits still get precise,
        // targeted updates; only edits an order of magnitude bigger (a full IFC reload, tens of
        // thousands of elements) still overflow into "something changed, re-sync yourself".
        private const int MaxChangedIds = 2000;
        private readonly HashSet<string> _pendingChangedIds = new HashSet<string>();
        // Added/modified are also tracked SEPARATELY from the union above (added-vs-modified is real
        // signal Revit already hands over via GetAddedElementIds()/GetModifiedElementIds() — merging
        // them lost it). Same overflow gate as the union: bounded by _pendingChangedIds' own count, so
        // one giant transaction drops ALL three id lists together, never a partial one.
        private readonly HashSet<string> _pendingAddedIds = new HashSet<string>();
        private readonly HashSet<string> _pendingModifiedIds = new HashSet<string>();
        // Deletions: e.GetDeletedElementIds() still yields numeric ids even though they no longer
        // resolve to an Element/UniqueId (the element is gone) — collected separately since there is
        // no doc.GetElement() cost to gate them behind (no UniqueId to resolve), only the same
        // "never send a misleadingly-partial list" cap.
        private readonly HashSet<long> _pendingDeletedIds = new HashSet<long>();
        // Revit's own name(s) for the operation(s) in this window ("Move Walls", "Change Type",
        // "Delete") — accumulated across the whole debounce window like the id sets, deduped, in
        // first-seen order (a List, not a HashSet, so that order survives).
        private readonly List<string> _pendingTransactionNames = new List<string>();
        private string? _pendingLastChangedBy;
        private bool _pendingIdsOverflowed;

        private readonly string _endpoint;
        private readonly string _token;

        private readonly object _gate = new object();
        private DateTime _lastChangedSentUtc = DateTime.MinValue;
        private Timer _changedTimer;
        private bool _hasPending;
        private ModelFacts _pending;

        public LoamEventClient()
        {
            var portVar = Environment.GetEnvironmentVariable("LOAM_HTTP_PORT");
            var port = int.TryParse(portVar, out var p) && p > 0 ? p : 47600;
            _endpoint = $"http://127.0.0.1:{port}/api/model-event";

            // Optional — only added if Loam later turns on sidecar-token enforcement.
            _token = Environment.GetEnvironmentVariable("LOAM_MODEL_EVENT_TOKEN");
        }

        /// <summary>Send an event now (opened / saved / closed / selection). Never throws.
        /// <paramref name="cause"/> (optional) — only meaningful on <c>kind: "saved"</c>, where it
        /// tells apart a Ctrl+S ("save") from a Sync to Central ("sync"); omitted from the payload
        /// otherwise. <c>kind</c> itself never changes so older orchestrator builds keep working.</summary>
        public void Send(string kind, ModelFacts facts, string? cause = null)
            => Post(kind, facts, cause);

        /// <summary>
        /// Record a DocumentChanged and emit at most one "changed" POST per window:
        /// leading-edge send when idle, plus a trailing send for continuous edits.
        /// <paramref name="addedIds"/>/<paramref name="modifiedIds"/> (optional) — the UniqueIds this
        /// transaction added/modified, kept SEPARATE (Revit already hands the two apart via
        /// GetAddedElementIds()/GetModifiedElementIds()) as well as unioned into the existing
        /// changedElementIds field for back-compat with older orchestrator builds.
        /// <paramref name="deletedElementIds"/> (optional) — numeric ElementIds this transaction
        /// deleted (no UniqueId survives a deletion, so these ride separately, never merged into the
        /// UniqueId sets above). <paramref name="transactionNames"/> (optional) — Revit's own name(s)
        /// for the operation(s) in this window. <paramref name="lastChangedBy"/> (optional) — who
        /// touched a representative element in this window, on a workshared model only. All are
        /// accumulated across the WHOLE debounce window, not just the latest transaction.
        /// </summary>
        public void SendChanged(
            ModelFacts facts,
            IEnumerable<string>? addedIds = null,
            IEnumerable<string>? modifiedIds = null,
            IEnumerable<long>? deletedElementIds = null,
            IEnumerable<string>? transactionNames = null,
            string? lastChangedBy = null)
        {
            lock (_gate)
            {
                _pending = facts;
                _hasPending = true;

                AddCappedLocked(addedIds, _pendingAddedIds);
                AddCappedLocked(modifiedIds, _pendingModifiedIds);

                if (deletedElementIds is not null)
                    foreach (var id in deletedElementIds)
                    {
                        if (_pendingDeletedIds.Count >= MaxChangedIds) { _pendingIdsOverflowed = true; break; }
                        _pendingDeletedIds.Add(id);
                    }

                if (transactionNames is not null)
                    foreach (var tn in transactionNames)
                        if (!string.IsNullOrEmpty(tn) && !_pendingTransactionNames.Contains(tn))
                            _pendingTransactionNames.Add(tn);

                // Last write wins for the window — good enough for "who's been editing", and avoids
                // carrying a list of names for what is, in practice, almost always one person's session.
                if (!string.IsNullOrEmpty(lastChangedBy)) _pendingLastChangedBy = lastChangedBy;

                var elapsed = DateTime.UtcNow - _lastChangedSentUtc;
                if (elapsed >= ChangedWindow)
                {
                    FlushChangedLocked();
                }
                else if (_changedTimer is null)
                {
                    var due = ChangedWindow - elapsed;
                    _changedTimer = new Timer(_ => OnChangedTimer(), null, due, Timeout.InfiniteTimeSpan);
                }
            }
        }

        /// <summary>Adds <paramref name="ids"/> into BOTH <paramref name="set"/> (the specific
        /// added/modified list) and the existing union set <see cref="_pendingChangedIds"/> (the
        /// back-compat changedElementIds field), gated by the union's own count so added, modified,
        /// and changedElementIds all overflow together — never a partial pair. Caller holds
        /// <see cref="_gate"/>.</summary>
        private void AddCappedLocked(IEnumerable<string>? ids, HashSet<string> set)
        {
            if (ids is null) return;
            foreach (var id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (_pendingChangedIds.Count >= MaxChangedIds) { _pendingIdsOverflowed = true; break; }
                set.Add(id);
                _pendingChangedIds.Add(id);
            }
        }

        private void OnChangedTimer()
        {
            lock (_gate)
            {
                _changedTimer?.Dispose();
                _changedTimer = null;
                if (_hasPending) FlushChangedLocked();
            }
        }

        /// <summary>
        /// Called from the host's Idling event (live request: "when Revit is idling, it can send extra
        /// data to Loam if needed — that would work both ways"). A pending "changed" batch otherwise only
        /// flushes on the Timer's own schedule, which can land at ANY moment — including mid-edit, the
        /// exact disruption the debounce window exists to avoid. If the window has already elapsed by the
        /// time Revit reports itself idle, flush right now instead of waiting for the Timer callback — a
        /// send then only ever happens when Revit is confirmed not busy. A no-op when nothing is pending or
        /// the window hasn't elapsed yet; the Timer remains the fallback for a long stretch with no idle tick.
        /// </summary>
        public void TryFlushIfIdle()
        {
            lock (_gate)
            {
                if (!_hasPending) return;
                if (DateTime.UtcNow - _lastChangedSentUtc < ChangedWindow) return;
                _changedTimer?.Dispose();
                _changedTimer = null;
                FlushChangedLocked();
            }
        }

        private void FlushChangedLocked()
        {
            _lastChangedSentUtc = DateTime.UtcNow;
            _hasPending = false;
            var facts = _pending;
            // Overflowed (a huge transaction) -> send NO ids, never a silently-truncated partial list; Loam
            // then falls back to its own bounded full sweep, exactly today's behaviour. transactionNames/
            // lastChangedBy aren't id lists, so they still ride along even on an overflowed window — they
            // describe the OPERATION, not the touched-element set, and cost nothing extra downstream.
            var overflowed = _pendingIdsOverflowed;
            var ids            = (!overflowed && _pendingChangedIds.Count   > 0) ? new List<string>(_pendingChangedIds)   : null;
            var addedIds       = (!overflowed && _pendingAddedIds.Count     > 0) ? new List<string>(_pendingAddedIds)     : null;
            var modifiedIds    = (!overflowed && _pendingModifiedIds.Count  > 0) ? new List<string>(_pendingModifiedIds)  : null;
            var deletedIds     = (!overflowed && _pendingDeletedIds.Count   > 0) ? new List<long>(_pendingDeletedIds)     : null;
            var transactionNames = _pendingTransactionNames.Count > 0 ? new List<string>(_pendingTransactionNames) : null;
            var lastChangedBy  = _pendingLastChangedBy;

            _pendingChangedIds.Clear();
            _pendingAddedIds.Clear();
            _pendingModifiedIds.Clear();
            _pendingDeletedIds.Clear();
            _pendingTransactionNames.Clear();
            _pendingLastChangedBy = null;
            _pendingIdsOverflowed = false;

            Post("changed", facts, null, ids, addedIds, modifiedIds, deletedIds, transactionNames, lastChangedBy);
        }

        private void Post(
            string kind, ModelFacts facts, string? cause,
            IReadOnlyList<string>? changedIds = null,
            IReadOnlyList<string>? addedIds = null,
            IReadOnlyList<string>? modifiedIds = null,
            IReadOnlyList<long>? deletedIds = null,
            IReadOnlyList<string>? transactionNames = null,
            string? lastChangedBy = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var body = new JsonObject { ["kind"] = kind };
                    if (!string.IsNullOrEmpty(facts?.Model))    body["model"]    = facts.Model;
                    if (!string.IsNullOrEmpty(facts?.Project))  body["project"]  = facts.Project;
                    if (!string.IsNullOrEmpty(facts?.Revision)) body["revision"] = facts.Revision;

                    // worksharing is always present — it's the field that lets Loam tell apart
                    // two users' local copies of the SAME model from two genuinely different
                    // models; see ModelFacts' own doc comment.
                    body["worksharing"] = facts?.Worksharing ?? "file_based_unknown";
                    if (!string.IsNullOrEmpty(facts?.CentralModelPath)) body["central_model_path"] = facts.CentralModelPath;
                    if (!string.IsNullOrEmpty(facts?.CloudProjectGuid)) body["cloud_project_guid"] = facts.CloudProjectGuid;
                    if (!string.IsNullOrEmpty(facts?.CloudModelGuid))   body["cloud_model_guid"]   = facts.CloudModelGuid;
                    if (!string.IsNullOrEmpty(facts?.CloudRegion))      body["cloud_region"]       = facts.CloudRegion;
                    // The single combined identity anchor (see ModelFacts.ModelInstanceId's own doc
                    // comment) — sent alongside the raw facts it's derived from so an older Loam that
                    // doesn't recognise this field yet still gets everything it already understood.
                    if (!string.IsNullOrEmpty(facts?.ModelInstanceId)) body["model_instance_id"] = facts.ModelInstanceId;

                    // cause only makes sense alongside kind: "saved" (save vs. sync) — omit it
                    // everywhere else rather than sending a meaningless field.
                    if (kind == "saved" && !string.IsNullOrEmpty(cause)) body["cause"] = cause;

                    if (changedIds is { Count: > 0 })
                    {
                        var arr = new JsonArray();
                        foreach (var id in changedIds) arr.Add(id);
                        body["changedElementIds"] = arr;
                    }
                    // Added-vs-modified, split (additive alongside the merged changedElementIds above,
                    // which stays for older orchestrator builds keyed off it) — Revit already hands
                    // these apart via GetAddedElementIds()/GetModifiedElementIds(); App.cs no longer
                    // merges them before they get here.
                    if (addedIds is { Count: > 0 })
                    {
                        var arr = new JsonArray();
                        foreach (var id in addedIds) arr.Add(id);
                        body["addedElementIds"] = arr;
                    }
                    if (modifiedIds is { Count: > 0 })
                    {
                        var arr = new JsonArray();
                        foreach (var id in modifiedIds) arr.Add(id);
                        body["modifiedElementIds"] = arr;
                    }
                    // Deletions: numeric ids only (no UniqueId survives a deletion) — Loam's ingest
                    // already has a consumer for this exact shape waiting.
                    if (deletedIds is { Count: > 0 })
                    {
                        var arr = new JsonArray();
                        foreach (var id in deletedIds) arr.Add(id);
                        body["deletedIds"] = arr;
                    }
                    // Revit's own name(s) for the operation(s) in this window ("Move Walls",
                    // "Change Type", "Delete") — literally why the change happened.
                    if (transactionNames is { Count: > 0 })
                    {
                        var arr = new JsonArray();
                        foreach (var tn in transactionNames) arr.Add(tn);
                        body["transactionNames"] = arr;
                    }
                    // Who touched it — workshared models only; omitted (never fabricated) on a
                    // non-workshared document, same "omit, don't guess" rule as ModelInstanceId.
                    if (!string.IsNullOrEmpty(lastChangedBy)) body["lastChangedBy"] = lastChangedBy;

                    using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                    {
                        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                    };
                    if (!string.IsNullOrEmpty(_token))
                        req.Headers.Add("X-Loam-Token", _token);

                    using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                }
                catch
                {
                    // Loam not running / connection refused / timeout — silent no-op by design.
                }
            });
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _changedTimer?.Dispose();
                _changedTimer = null;
            }
        }
    }
}
