using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Loam.Revit.Connector.RevitBridge
{
    /// <summary>
    /// The add-in's own settings file — replaces the <c>LOAM_REVIT_LISTEN</c>/<c>LOAM_REVIT_TOKEN</c>
    /// environment variables. <c>README.md</c> documented <c>MYCELIUM_REVIT_LISTEN</c>/<c>_TOKEN</c>,
    /// which <c>src/App.cs</c> never read — so following the README silently ran the MCP server with
    /// NO bearer auth (see the handoff plan's "Fix first (security)" note, and ROADMAP.md's own
    /// changelog entry for the same env-var-name drift in the docs). A settings file is also where
    /// docs/MODEL_LOG.md's "make the log root configurable... not an environment variable" lives.
    ///
    /// Location: <c>%LOCALAPPDATA%\Loam\RevitConnector\settings.json</c>, or
    /// <c>LOAM_REVIT_SETTINGS_PATH</c> to override it (tests/CI only — not documented to end users
    /// as the primary configuration path; the point of this class is that the *token* is never an
    /// environment variable).
    ///
    /// SECURITY: the MCP server never starts without a bearer token. A missing settings file gets
    /// one auto-provisioned (a fresh random token) on first run, so a normal install is
    /// authenticated by default with no manual step. A settings file that exists but has an
    /// explicitly blank token, or fails to parse, is treated as a deliberate (mis)configuration and
    /// refused — see <see cref="ExplicitlyNoAuth"/> — rather than silently falling back to no auth
    /// like the bug this replaces.
    /// </summary>
    public sealed class ConnectorSettings
    {
        public string Listen { get; set; } = "http://127.0.0.1:47100/mcp";
        public string? Token { get; set; }
        public string? ModelLogRoot { get; set; }

        /// <summary>True when this settings file exists but explicitly carries an empty/blank
        /// token (or failed to parse) — the caller must refuse to start the MCP server in this
        /// case, never fall back to running unauthenticated.</summary>
        [JsonIgnore]
        public bool ExplicitlyNoAuth { get; private set; }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string DefaultPath()
        {
            var overridePath = Environment.GetEnvironmentVariable("LOAM_REVIT_SETTINGS_PATH");
            if (!string.IsNullOrEmpty(overridePath)) return overridePath;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Loam", "RevitConnector", "settings.json");
        }

        /// <summary>Loads settings from <paramref name="path"/>, creating a fresh file with an
        /// auto-generated token when none exists yet.</summary>
        public static ConnectorSettings LoadOrCreate(string path)
        {
            if (!File.Exists(path))
            {
                var fresh = new ConnectorSettings { Token = GenerateToken() };
                Save(path, fresh);
                return fresh;
            }

            ConnectorSettings settings;
            try
            {
                var text = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<ConnectorSettings>(text, JsonOptions) ?? new ConnectorSettings();
            }
            catch
            {
                // Corrupt settings file — never fall back to running unauthenticated; treat it
                // the same as an explicitly-blank token so OnStartup refuses to start.
                settings = new ConnectorSettings { Token = null };
            }

            settings.ExplicitlyNoAuth = string.IsNullOrWhiteSpace(settings.Token);
            return settings;
        }

        private static void Save(string path, ConnectorSettings settings)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }

        private static string GenerateToken()
        {
            var bytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }
    }
}
