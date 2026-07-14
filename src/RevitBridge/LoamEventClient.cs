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
        // touched more elements than a batch lookup is worth (Purge Unused, an IFC reload) — the list is
        // dropped entirely (never silently truncated) so Loam falls back to its own bounded full sweep
        // instead of acting on a partial, misleadingly-complete-looking id list.
        private const int MaxChangedIds = 300;
        private readonly HashSet<string> _pendingChangedIds = new HashSet<string>();
        private bool _pendingIdsOverflowed;

        private readonly string _endpoint;
        private readonly string _token;

        private readonly object _gate = new object();
        private DateTime _lastChangedSentUtc = DateTime.MinValue;
        private Timer _changedTimer;
        private bool _hasPending;
        private (string model, string project, string revision) _pending;

        public LoamEventClient()
        {
            var portVar = Environment.GetEnvironmentVariable("LOAM_HTTP_PORT");
            var port = int.TryParse(portVar, out var p) && p > 0 ? p : 47600;
            _endpoint = $"http://127.0.0.1:{port}/api/model-event";

            // Optional — only added if Loam later turns on sidecar-token enforcement.
            _token = Environment.GetEnvironmentVariable("LOAM_MODEL_EVENT_TOKEN");
        }

        /// <summary>Send an event now (opened / saved / closed / selection). Never throws.</summary>
        public void Send(string kind, string model, string project, string revision)
            => Post(kind, model, project, revision);

        /// <summary>
        /// Record a DocumentChanged and emit at most one "changed" POST per window:
        /// leading-edge send when idle, plus a trailing send for continuous edits.
        /// <paramref name="changedIds"/> (optional) — the UniqueIds this transaction touched (added or
        /// modified elements only; a deleted element has no UniqueId left to report). Unioned into the
        /// pending set across the whole debounce window.
        /// </summary>
        public void SendChanged(string model, string project, string revision, IEnumerable<string> changedIds = null)
        {
            lock (_gate)
            {
                _pending = (model, project, revision);
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

        private void FlushChangedLocked()
        {
            _lastChangedSentUtc = DateTime.UtcNow;
            _hasPending = false;
            var (model, project, revision) = _pending;
            // Overflowed (a huge transaction) -> send NO ids, never a silently-truncated partial list; Loam
            // then falls back to its own bounded full sweep, exactly today's behaviour.
            var ids = (!_pendingIdsOverflowed && _pendingChangedIds.Count > 0) ? new List<string>(_pendingChangedIds) : null;
            _pendingChangedIds.Clear();
            _pendingIdsOverflowed = false;
            Post("changed", model, project, revision, ids);
        }

        private void Post(string kind, string model, string project, string revision, IReadOnlyList<string> changedIds = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var body = new JsonObject { ["kind"] = kind };
                    if (!string.IsNullOrEmpty(model))    body["model"]    = model;
                    if (!string.IsNullOrEmpty(project))  body["project"]  = project;
                    if (!string.IsNullOrEmpty(revision)) body["revision"] = revision;
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
