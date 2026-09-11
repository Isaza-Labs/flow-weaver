using System.Diagnostics;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Net;

namespace flow_weaver_backend.Services.Email;

// Builds and delivers one MIME message through a configured EmailChannel.
// Shared by the channel test-send endpoint and the email_send snippet handler
// so both paths negotiate TLS, authenticate and report failures identically.
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(
        EmailChannel channel, EmailMessage message, CancellationToken ct = default);
}

public sealed class EmailSender : IEmailSender
{
    // A stuck relay must not pin a worker slot for the whole step timeout.
    private const int SocketTimeoutMs = 30_000;

    private readonly ICredentialEncryptionService _crypto;
    private readonly IUrlGuard _urlGuard;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(
        ICredentialEncryptionService crypto,
        IUrlGuard urlGuard,
        ILogger<EmailSender> logger)
    {
        _crypto = crypto;
        _urlGuard = urlGuard;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(
        EmailChannel channel, EmailMessage message, CancellationToken ct = default)
    {
        MimeMessage mime;
        try
        {
            mime = BuildMime(channel, message);
        }
        catch (FormatException ex)
        {
            // MailboxAddress.Parse rejects the address — a config error, not a
            // transport failure, and worth surfacing verbatim.
            return EmailSendResult.Failure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return EmailSendResult.Failure(ex.Message);
        }

        // SSRF guard. An admin-supplied SMTP host is user-supplied egress just
        // like an Integration base URL: without this, "send a test mail" is a
        // port scanner against the cluster's private network. AllowPrivateNetwork
        // relaxes RFC-1918 only; loopback and 169.254.169.254 stay blocked.
        //
        // EnsureHostSafe, not EnsureSafe: this is a host, not a URL. It used to
        // pass a synthetic `smtp://host:port`, which worked only because the
        // guard ignored the scheme — the day EnsureSafe grew an http/https
        // allowlist, every send failed with "scheme 'smtp' is not allowed".
        try
        {
            _urlGuard.EnsureHostSafe(channel.Host, allowPrivate: channel.AllowPrivateNetwork);
        }
        catch (InvalidOperationException ex)
        {
            return EmailSendResult.Failure(ex.Message);
        }

        var sw = Stopwatch.StartNew();
        using var client = new SmtpClient { Timeout = SocketTimeoutMs };

        if (channel.TlsSkipVerify)
        {
            // Opt-in per channel, same trade-off as Integration.TLSSkipVerify:
            // an internal relay with a self-signed certificate.
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }

        try
        {
            await client.ConnectAsync(
                channel.Host, channel.Port, SocketOptions(channel.Security), ct);

            var username = channel.Username;
            if (!string.IsNullOrWhiteSpace(username))
            {
                var password = _crypto.Decrypt(channel.EncryptedPassword) ?? string.Empty;
                await client.AuthenticateAsync(username, password, ct);
            }

            var response = await client.SendAsync(mime, ct);
            await client.DisconnectAsync(quit: true, ct);
            sw.Stop();

            _logger.LogInformation(
                "email.send.ok channel_id={ChannelId} provider={Provider} host={Host} recipients={Recipients} elapsed_ms={ElapsedMs}",
                channel.EmailChannelId, channel.Provider, channel.Host,
                message.To.Count + message.Cc.Count + message.Bcc.Count, sw.ElapsedMilliseconds);

            return new EmailSendResult(true, mime.MessageId, response, null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is AuthenticationException or SmtpCommandException
            or SmtpProtocolException or SslHandshakeException or SocketException
            or IOException or TimeoutException or NotSupportedException)
        {
            sw.Stop();
            var error = Describe(ex);
            // Host, not credentials. Decrypted passwords never reach a log line.
            _logger.LogWarning(
                "email.send.failed channel_id={ChannelId} provider={Provider} host={Host} port={Port} security={Security} error={Error}",
                channel.EmailChannelId, channel.Provider, channel.Host, channel.Port,
                channel.Security, error);
            return EmailSendResult.Failure(error, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    // MailKit's exception messages are decent but drop the part operators need
    // most (which stage failed), so prefix it.
    private static string Describe(Exception ex) => ex switch
    {
        AuthenticationException => $"SMTP authentication failed: {ex.Message}",
        SslHandshakeException => $"TLS handshake failed: {ex.Message}. "
            + "Check the `security` mode matches the port (starttls→587, ssl→465).",
        SmtpCommandException sce => $"SMTP command rejected ({sce.StatusCode}): {sce.Message}",
        SmtpProtocolException => $"SMTP protocol error: {ex.Message}",
        SocketException or IOException => $"cannot reach the SMTP server: {ex.Message}",
        TimeoutException => $"SMTP server timed out after {SocketTimeoutMs / 1000}s",
        _ => ex.Message,
    };

    internal static SecureSocketOptions SocketOptions(string? security) => security switch
    {
        EmailChannel.SecuritySsl => SecureSocketOptions.SslOnConnect,
        EmailChannel.SecurityNone => SecureSocketOptions.None,
        // starttls and anything unrecognised: require the upgrade rather than
        // silently falling back to plaintext (StartTlsWhenAvailable would let a
        // MITM strip the STARTTLS advertisement).
        _ => SecureSocketOptions.StartTls,
    };

    internal static MimeMessage BuildMime(EmailChannel channel, EmailMessage message)
    {
        var fromAddress = Coalesce(message.FromAddress, channel.FromAddress);
        if (string.IsNullOrWhiteSpace(fromAddress))
            throw new ArgumentException("the channel has no from_address and the message did not supply one");

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(
            Coalesce(message.FromName, channel.FromName) ?? string.Empty, fromAddress));

        AddAll(mime.To, message.To);
        AddAll(mime.Cc, message.Cc);
        AddAll(mime.Bcc, message.Bcc);
        if (mime.To.Count == 0 && mime.Cc.Count == 0 && mime.Bcc.Count == 0)
            throw new ArgumentException("at least one recipient is required (to, cc or bcc)");

        var replyTo = Coalesce(message.ReplyTo, channel.ReplyTo);
        if (!string.IsNullOrWhiteSpace(replyTo))
            mime.ReplyTo.Add(MailboxAddress.Parse(replyTo));

        mime.Subject = message.Subject ?? string.Empty;

        var hasHtml = !string.IsNullOrWhiteSpace(message.HtmlBody);
        var hasText = !string.IsNullOrWhiteSpace(message.TextBody);
        if (!hasHtml && !hasText)
            throw new ArgumentException("body or html is required");

        var builder = new BodyBuilder();
        if (hasHtml) builder.HtmlBody = message.HtmlBody;
        if (hasText) builder.TextBody = message.TextBody;
        // An html-only message still gets a text/plain part: many relays and
        // spam filters penalise html-only mail.
        if (hasHtml && !hasText)
            builder.TextBody = HtmlToText(message.HtmlBody!);

        foreach (var att in message.Attachments)
        {
            if (string.IsNullOrWhiteSpace(att.FileName))
                throw new ArgumentException("every attachment needs a file_name");
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(att.ContentBase64 ?? string.Empty);
            }
            catch (FormatException)
            {
                throw new ArgumentException(
                    $"attachment '{att.FileName}': content_base64 is not valid base64");
            }
            var contentType = string.IsNullOrWhiteSpace(att.ContentType)
                ? "application/octet-stream"
                : att.ContentType;
            builder.Attachments.Add(att.FileName, bytes, ContentType.Parse(contentType));
        }

        mime.Body = builder.ToMessageBody();
        return mime;
    }

    private static void AddAll(InternetAddressList list, IReadOnlyList<string> addresses)
    {
        foreach (var raw in addresses)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            list.Add(MailboxAddress.Parse(raw.Trim()));
        }
    }

    // Crude but dependency-free: strip tags so the text/plain alternative is
    // readable. Anything richer belongs in the workflow that composed the html.
    private static string HtmlToText(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(
            html, "<(br|/p|/div|/tr|/li)[^>]*>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private static string? Coalesce(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}
