using System.Text.Json.Serialization;
using DesktopPlus.Companion;

namespace DesktopPlus
{
    /// <summary>
    /// Persisted configuration for the phone companion server.
    /// The TLS certificate is stored separately on disk (see CompanionCertificate).
    /// </summary>
    public class CompanionSettings
    {
        public bool Enabled { get; set; } = false;
        public int Port { get; set; } = 8443;

        /// <summary>Runtime (plaintext) token. Never written to disk directly.</summary>
        [JsonIgnore]
        public string Token { get; set; } = "";

        /// <summary>
        /// Serialized form of the token: DPAPI-encrypted (CurrentUser). This keeps the
        /// access-granting token out of the settings file as readable text.
        /// </summary>
        [JsonPropertyName("protectedToken")]
        public string ProtectedToken
        {
            get => CompanionDataProtector.ProtectString(Token);
            set => Token = CompanionDataProtector.UnprotectString(value);
        }
    }
}
