using flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Email;

// Connection defaults per SMTP provider. Choosing a provider in the UI
// pre-fills host/port/security so the operator only supplies credentials;
// every field stays overridable and what gets persisted are the effective
// values, never the preset key.
//
// UsernameHint / PasswordHint drive the field labels and the inline help — a
// SendGrid "username" is always the literal `apikey`, an SES one is an IAM
// SMTP credential, and a Gmail password must be an App Password because
// Google removed basic-auth SMTP.
public sealed record EmailProviderPreset(
    string Provider,
    string Label,
    string Host,
    int Port,
    string Security,
    string? FixedUsername,
    string UsernameHint,
    string PasswordHint,
    string? DocsUrl)
{
    public static readonly IReadOnlyList<EmailProviderPreset> All = new[]
    {
        new EmailProviderPreset(
            EmailChannel.ProviderGmail, "Gmail / Google Workspace",
            "smtp.gmail.com", 587, EmailChannel.SecurityStartTls,
            FixedUsername: null,
            UsernameHint: "Your full Gmail address (e.g. alerts@example.com).",
            PasswordHint: "A 16-character App Password — Google rejects the account password. "
                + "Requires 2-Step Verification on the account.",
            DocsUrl: "https://support.google.com/accounts/answer/185833"),

        new EmailProviderPreset(
            EmailChannel.ProviderOutlook365, "Outlook / Microsoft 365",
            "smtp.office365.com", 587, EmailChannel.SecurityStartTls,
            FixedUsername: null,
            UsernameHint: "The full mailbox address (UPN) that will send.",
            PasswordHint: "The mailbox password, or an app password when MFA is on. "
                + "SMTP AUTH must be enabled for the mailbox in Exchange Online.",
            DocsUrl: "https://learn.microsoft.com/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission"),

        new EmailProviderPreset(
            EmailChannel.ProviderSendGrid, "SendGrid",
            "smtp.sendgrid.net", 587, EmailChannel.SecurityStartTls,
            FixedUsername: "apikey",
            UsernameHint: "Always the literal string `apikey`.",
            PasswordHint: "A SendGrid API key with the Mail Send permission.",
            DocsUrl: "https://www.twilio.com/docs/sendgrid/for-developers/sending-email/integrating-with-the-smtp-api"),

        new EmailProviderPreset(
            EmailChannel.ProviderSes, "Amazon SES",
            "email-smtp.us-east-1.amazonaws.com", 587, EmailChannel.SecurityStartTls,
            FixedUsername: null,
            UsernameHint: "The SES SMTP username (an IAM SMTP credential, not an access key id).",
            PasswordHint: "The SES SMTP password derived from the IAM secret.",
            DocsUrl: "https://docs.aws.amazon.com/ses/latest/dg/smtp-credentials.html"),

        new EmailProviderPreset(
            EmailChannel.ProviderMailgun, "Mailgun",
            "smtp.mailgun.org", 587, EmailChannel.SecurityStartTls,
            FixedUsername: null,
            UsernameHint: "The domain SMTP login (e.g. postmaster@mg.example.com).",
            PasswordHint: "The SMTP password shown on the Mailgun domain page.",
            DocsUrl: "https://documentation.mailgun.com/docs/mailgun/user-manual/sending-messages/#smtp-relay"),

        new EmailProviderPreset(
            EmailChannel.ProviderSmtp, "Custom SMTP server",
            Host: string.Empty, 587, EmailChannel.SecurityStartTls,
            FixedUsername: null,
            UsernameHint: "SMTP AUTH user. Leave empty for an unauthenticated internal relay.",
            PasswordHint: "SMTP AUTH password. Leave empty for an unauthenticated internal relay.",
            DocsUrl: null),
    };

    private static readonly IReadOnlyDictionary<string, EmailProviderPreset> ByProvider =
        All.ToDictionary(p => p.Provider, StringComparer.OrdinalIgnoreCase);

    public static EmailProviderPreset? Find(string? provider) =>
        provider is not null && ByProvider.TryGetValue(provider, out var p) ? p : null;

    public static bool IsKnown(string? provider) => Find(provider) is not null;

    public static IEnumerable<string> Providers => All.Select(p => p.Provider);
}
