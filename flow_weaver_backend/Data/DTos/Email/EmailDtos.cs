using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos.Email;

// ─── Channel CRUD ───────────────────────────────────────────────────

public class CreateEmailChannel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // gmail | outlook365 | sendgrid | ses | mailgun | smtp. Selects the
    // connection preset; host/port/security below override it when supplied.
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    // starttls | ssl | none
    [JsonPropertyName("security")]
    public string? Security { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    // Plaintext — encrypted at the service layer, never returned.
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("from_address")]
    public string? FromAddress { get; set; }

    [JsonPropertyName("from_name")]
    public string? FromName { get; set; }

    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; set; }

    [JsonPropertyName("allow_private_network")]
    public bool? AllowPrivateNetwork { get; set; }

    [JsonPropertyName("tls_skip_verify")]
    public bool? TlsSkipVerify { get; set; }

    [JsonPropertyName("is_default")]
    public bool? IsDefault { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}

// Update reuses the create shape; null fields are left unchanged. A non-null
// (even empty) password rotates the secret — empty clears it.
public class UpdateEmailChannel : CreateEmailChannel
{
}

public class EmailChannelResponse
{
    [JsonPropertyName("email_channel_id")]
    public Guid EmailChannelId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("security")]
    public string Security { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    // Boolean instead of the secret itself.
    [JsonPropertyName("has_password")]
    public bool HasPassword { get; set; }

    [JsonPropertyName("from_address")]
    public string FromAddress { get; set; } = string.Empty;

    [JsonPropertyName("from_name")]
    public string? FromName { get; set; }

    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; set; }

    [JsonPropertyName("allow_private_network")]
    public bool AllowPrivateNetwork { get; set; }

    [JsonPropertyName("tls_skip_verify")]
    public bool TlsSkipVerify { get; set; }

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("last_send_at")]
    public DateTime? LastSendAt { get; set; }

    [JsonPropertyName("last_send_status")]
    public string? LastSendStatus { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

// ─── Provider presets (drives the "new channel" form) ────────────────

public class EmailProviderPresetResponse
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("security")]
    public string Security { get; set; } = string.Empty;

    // Non-null when the provider mandates a literal username (SendGrid: "apikey").
    [JsonPropertyName("fixed_username")]
    public string? FixedUsername { get; set; }

    [JsonPropertyName("username_hint")]
    public string UsernameHint { get; set; } = string.Empty;

    [JsonPropertyName("password_hint")]
    public string PasswordHint { get; set; } = string.Empty;

    [JsonPropertyName("docs_url")]
    public string? DocsUrl { get; set; }
}

// ─── Test send ──────────────────────────────────────────────────────

public class TestEmailChannelRequest
{
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }
}

public class TestEmailChannelResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    // SMTP server response / transcript tail. Never contains credentials.
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; set; }
}
