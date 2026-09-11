namespace flow_weaver_backend.Services.Messaging;

// Bind to the "Messaging" section of appsettings. All values have sane defaults
// so the section can be omitted.
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    // Public base URL the providers should POST their webhooks to. Used only to
    // render setup instructions / the account-linking link in the admin UI and
    // the bot's reply (e.g. https://app.example.com). No trailing slash.
    public string PublicBaseUrl { get; set; } = string.Empty;

    // Account-linking token lifetime.
    public int LinkTokenTtlMinutes { get; set; } = 15;

    // Refuse to enqueue more inbound work once the job queue is this deep
    // (backpressure), mirroring GitWebhookBackpressure.
    public int MaxQueueDepth { get; set; } = 500;

    // Outbound send retries before a delivery is marked failed (F7).
    public int MaxSendAttempts { get; set; } = 3;

    // Reject inbound deliveries whose provider timestamp is older than this
    // (anti-replay; Slack/WhatsApp carry a timestamp). 0 disables the check.
    public int MaxRequestAgeSeconds { get; set; } = 300;

    // Retention sweeper: delete inbound/delivery audit rows older than this and
    // link tokens past expiry. 0 disables the sweeper.
    public int RetentionDays { get; set; } = 90;
}
