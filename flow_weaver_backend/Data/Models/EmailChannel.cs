using System.Text.Json.Serialization;

namespace flow_weaver_backend.Models;

// A configured outbound SMTP relay. One row per provider account (a Gmail
// mailbox, an Office 365 tenant, a SendGrid API key, a self-hosted Postfix).
//
// `Provider` only selects the connection PRESET (host/port/security defaults
// and the username convention); the stored Host/Port/Security are always the
// effective values, so a channel keeps working if a preset is later retuned.
// The password is encrypted at rest by CredentialEncryptionService and never
// leaves the backend — responses expose only `has_password`.
public class EmailChannel : BaseModel
{
    public const string ProviderGmail = "gmail";
    public const string ProviderOutlook365 = "outlook365";
    public const string ProviderSendGrid = "sendgrid";
    public const string ProviderSes = "ses";
    public const string ProviderMailgun = "mailgun";
    public const string ProviderSmtp = "smtp";

    // Transport security. `starttls` upgrades a plaintext connection on 587,
    // `ssl` is implicit TLS on 465, `none` is unencrypted SMTP on 25 — only
    // sane for an internal relay, and the service refuses `none` with a
    // password unless the operator opted into a private-network target.
    public const string SecurityStartTls = "starttls";
    public const string SecuritySsl = "ssl";
    public const string SecurityNone = "none";

    public Guid EmailChannelId { get; set; }

    public string Name { get; set; } = string.Empty;

    // gmail | outlook365 | sendgrid | ses | mailgun | smtp
    public string Provider { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Security { get; set; } = SecurityStartTls;

    // SMTP AUTH username. Empty means "no authentication" (open internal relay).
    public string? Username { get; set; }

    // SMTP AUTH password / app password / API key. Encrypted at rest.
    [JsonIgnore]
    public byte[]? EncryptedPassword { get; set; }

    // Envelope + header sender. Providers reject a From that isn't an owned
    // identity, so this is per-channel rather than per-message; a message may
    // still override it when the provider allows it.
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string? ReplyTo { get; set; }

    // Mirrors Integration.AllowPrivateNetwork: a self-hosted relay usually
    // lives on RFC-1918, which the SSRF guard blocks by default. Loopback and
    // the 169.254.169.254 metadata IP stay blocked regardless.
    public bool AllowPrivateNetwork { get; set; }

    // Mirrors Integration.TLSSkipVerify — accept a self-signed certificate on
    // an internal relay. Never set this on a public provider.
    public bool TlsSkipVerify { get; set; }

    // The channel an email_send step uses when it does not name one. At most
    // one active row may hold it; the service demotes the previous default.
    public bool IsDefault { get; set; }

    public bool Enabled { get; set; }

    public DateTime? LastSendAt { get; set; }
    public string? LastSendStatus { get; set; }
}
