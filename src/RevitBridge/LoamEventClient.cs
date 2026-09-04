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
        /// <paramref name="changedIds"/> (optional) — the UniqueIds this transaction touched (added or
        /// modified elements only; a deleted element has no UniqueId left to report). Unioned into the
        /// pending set across the whole debounce window.
        /// </summary>
        public void SendChanged(ModelFacts facts, IEnumerable<string>? changedIds = null)
        {
            lock (_gate)
            {
                _pending = facts;
                _hasPending = true;
                if (changedIds is not null)
                {
                    foreach (var id in changedIds)
                    {
                        if (string.IsNullOrEmpty(id)) continue;
                        if (_pendingChangedIds.Count >= MaxChangedIds) { _pendingIdsOverflowed = true; break; }
                        _pendingChangedIds.Add(id);
                    }
                }

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
            // then falls back to its own bounded full sweep, exactly today's behaviour.
            var ids = (!_pendingIdsOverflowed && _pendingChangedIds.Count > 0) ? new List<string>(_pendingChangedIds) : null;
            _pendingChangedIds.Clear();
            _pendingIdsOverflowed = false;
            Post("changed", facts, null, ids);
        }

        private void Post(string kind, ModelFacts facts, string? cause, IReadOnlyList<string>? changedIds = null)
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

                    // cause only makes sense alongside kind: "saved" (save vs. sync) — omit it
                    // everywhere else rather than sending a meaningless field.
                    if (kind == "saved" && !string.IsNullOrEmpty(cause)) body["cause"] = cause;

                    if (changedIds is { Count: > 0 })
                    {
                        var arr = new JsonArray();
                        foreach (var id in changedIds) arr.Add(id);
                        body["changedElementIds"] = arr;
                    }

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
