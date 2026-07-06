using System;
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
        /// </summary>
        public void SendChanged(string model, string project, string revision)
        {
            lock (_gate)
            {
                _pending = (model, project, revision);
                _hasPending = true;

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
            Post("changed", model, project, revision);
        }

        private void Post(string kind, string model, string project, string revision)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var body = new JsonObject { ["kind"] = kind };
                    if (!string.IsNullOrEmpty(model))    body["model"]    = model;
                    if (!string.IsNullOrEmpty(project))  body["project"]  = project;
                    if (!string.IsNullOrEmpty(revision)) body["revision"] = revision;

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
