using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Email;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Email;

// Admin CRUD for outbound SMTP channels plus a live test-send. The password is
// encrypted on write and never returned (only has_password). Choosing a
// provider fills in the connection preset; the row always stores the effective
// host/port/security so a later preset change can't silently repoint traffic.
public interface IEmailChannelService
{
    Task<ActionResult<ListResponse<EmailChannelResponse>>> ListAsync(int limit, int offset);
    Task<ActionResult<EmailChannelResponse>> GetAsync(Guid id);
    Task<ActionResult<EmailChannelResponse>> CreateAsync(CreateEmailChannel dto);
    Task<ActionResult<EmailChannelResponse>> UpdateAsync(Guid id, UpdateEmailChannel dto);
    Task<IActionResult> DeleteAsync(Guid id);
    Task<ActionResult<TestEmailChannelResponse>> TestAsync(
        Guid id, TestEmailChannelRequest dto, CancellationToken ct = default);
    ActionResult<ListResponse<EmailProviderPresetResponse>> ListPresets();
}

public class EmailChannelService : IEmailChannelService
{
    private static readonly HashSet<string> KnownSecurity = new(StringComparer.OrdinalIgnoreCase)
    {
        EmailChannel.SecurityStartTls, EmailChannel.SecuritySsl, EmailChannel.SecurityNone,
    };

    // Deliberately permissive — the authority on address validity is the SMTP
    // server, and MailboxAddress.Parse runs before any send. This only catches
    // the obvious typo at config time.
    private static readonly Regex AddressShape =
        new(@"^[^@\s<>]+@[^@\s<>.]+(\.[^@\s<>.]+)+$", RegexOptions.Compiled);

    private readonly IEmailChannelRepository _channels;
    private readonly ICredentialEncryptionService _crypto;
    private readonly IEmailSender _sender;
    private readonly IAuditLogger _auditLog;

    public EmailChannelService(
        IEmailChannelRepository channels,
        ICredentialEncryptionService crypto,
        IEmailSender sender,
        IAuditLogger auditLog)
    {
        _channels = channels;
        _crypto = crypto;
        _sender = sender;
        _auditLog = auditLog;
    }

    // A channel is an authenticated egress path with a stored credential: which
    // relay it points at, whether TLS is verified, and whether it may reach the
    // private network are the security posture. The password never appears.
    private static object ChannelAudit(EmailChannel c) => new
    {
        c.Name,
        c.Provider,
        c.Host,
        c.Port,
        c.Security,
        c.Username,
        from_address = c.FromAddress,
        allow_private_network = c.AllowPrivateNetwork,
        tls_skip_verify = c.TlsSkipVerify,
        is_default = c.IsDefault,
        c.Enabled,
        has_password = c.EncryptedPassword is { Length: > 0 },
    };

    public async Task<ActionResult<ListResponse<EmailChannelResponse>>> ListAsync(int limit, int offset)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var total = await _channels.CountAsync();
        var rows = await _channels.ListAsync(limit, offset);
        return new OkObjectResult(new ListResponse<EmailChannelResponse>
        {
            Data = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<EmailChannelResponse>> GetAsync(Guid id)
    {
        var channel = await _channels.GetByIdAsync(id, tracking: false)
            ?? throw new NotFoundException("email channel", id);
        return ToResponse(channel);
    }

    public ActionResult<ListResponse<EmailProviderPresetResponse>> ListPresets()
    {
        var data = EmailProviderPreset.All.Select(p => new EmailProviderPresetResponse
        {
            Provider = p.Provider,
            Label = p.Label,
            Host = p.Host,
            Port = p.Port,
            Security = p.Security,
            FixedUsername = p.FixedUsername,
            UsernameHint = p.UsernameHint,
            PasswordHint = p.PasswordHint,
            DocsUrl = p.DocsUrl,
        }).ToList();
        return new OkObjectResult(new ListResponse<EmailProviderPresetResponse>
        {
            Data = data,
            Total = data.Count,
            Limit = data.Count,
            Offset = 0,
        });
    }

    public async Task<ActionResult<EmailChannelResponse>> CreateAsync(CreateEmailChannel dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new ValidationException("name is required", code: "name_required");

        var preset = EmailProviderPreset.Find(dto.Provider)
            ?? throw new ValidationException(
                $"provider must be one of: {string.Join(", ", EmailProviderPreset.Providers)}",
                code: "invalid_provider");

        var name = dto.Name.Trim();
        if (await _channels.NameExistsAsync(name))
            throw new ValidationException(
                $"an email channel named '{name}' already exists", code: "duplicate_name");

        // The preset fills only what the caller left blank, so a Gmail channel
        // pointed at a corporate relay keeps the operator's host.
        var host = Blank(dto.Host) ? preset.Host : dto.Host!.Trim();
        var security = Blank(dto.Security) ? preset.Security : dto.Security!.Trim().ToLowerInvariant();
        var port = dto.Port ?? preset.Port;
        var username = preset.FixedUsername ?? dto.Username?.Trim();

        ValidateConnection(host, port, security);
        ValidateIdentity(dto.FromAddress, dto.ReplyTo);
        ValidateAuth(username, dto.Password, security, dto.AllowPrivateNetwork ?? false);

        var now = DateTime.UtcNow;
        var channel = new EmailChannel
        {
            EmailChannelId = Guid.NewGuid(),
            Name = name,
            Provider = preset.Provider,
            Host = host,
            Port = port,
            Security = security,
            Username = Blank(username) ? null : username,
            EncryptedPassword = string.IsNullOrEmpty(dto.Password) ? null : _crypto.Encrypt(dto.Password),
            FromAddress = dto.FromAddress?.Trim() ?? string.Empty,
            FromName = Blank(dto.FromName) ? null : dto.FromName!.Trim(),
            ReplyTo = Blank(dto.ReplyTo) ? null : dto.ReplyTo!.Trim(),
            AllowPrivateNetwork = dto.AllowPrivateNetwork ?? false,
            TlsSkipVerify = dto.TlsSkipVerify ?? false,
            IsDefault = dto.IsDefault ?? false,
            Enabled = dto.Enabled ?? true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Promote before Add so the demotion and the insert land in one save.
        if (channel.IsDefault) await DemoteExistingDefaultsAsync(exceptId: null);
        _channels.Add(channel);
        await _channels.SaveChangesAsync();

        await _auditLog.LogAsync("email_channel", channel.EmailChannelId, "create",
            after: ChannelAudit(channel));

        return ToResponse(channel);
    }

    public async Task<ActionResult<EmailChannelResponse>> UpdateAsync(Guid id, UpdateEmailChannel dto)
    {
        var channel = await _channels.GetByIdAsync(id, tracking: true)
            ?? throw new NotFoundException("email channel", id);

        var auditBefore = ChannelAudit(channel);

        if (!Blank(dto.Name))
        {
            var name = dto.Name.Trim();
            if (await _channels.NameExistsAsync(name, exceptId: id))
                throw new ValidationException(
                    $"an email channel named '{name}' already exists", code: "duplicate_name");
            channel.Name = name;
        }

        if (!Blank(dto.Provider))
        {
            var preset = EmailProviderPreset.Find(dto.Provider)
                ?? throw new ValidationException(
                    $"provider must be one of: {string.Join(", ", EmailProviderPreset.Providers)}",
                    code: "invalid_provider");
            channel.Provider = preset.Provider;
            // Switching provider re-applies the preset to whatever the caller
            // did NOT send in the same request; explicit fields still win.
            if (Blank(dto.Host)) channel.Host = preset.Host;
            if (dto.Port is null) channel.Port = preset.Port;
            if (Blank(dto.Security)) channel.Security = preset.Security;
            if (preset.FixedUsername is not null) channel.Username = preset.FixedUsername;
        }

        if (!Blank(dto.Host)) channel.Host = dto.Host!.Trim();
        if (dto.Port is not null) channel.Port = dto.Port.Value;
        if (!Blank(dto.Security)) channel.Security = dto.Security!.Trim().ToLowerInvariant();
        // Username is nullable-clearable: an empty string drops SMTP AUTH.
        if (dto.Username is not null)
            channel.Username = dto.Username.Length == 0 ? null : dto.Username.Trim();
        // A non-null password rotates it; empty clears it; null leaves it alone.
        if (dto.Password is not null)
            channel.EncryptedPassword = dto.Password.Length == 0 ? null : _crypto.Encrypt(dto.Password);
        if (dto.FromAddress is not null) channel.FromAddress = dto.FromAddress.Trim();
        if (dto.FromName is not null)
            channel.FromName = dto.FromName.Length == 0 ? null : dto.FromName.Trim();
        if (dto.ReplyTo is not null)
            channel.ReplyTo = dto.ReplyTo.Length == 0 ? null : dto.ReplyTo.Trim();
        if (dto.AllowPrivateNetwork is not null) channel.AllowPrivateNetwork = dto.AllowPrivateNetwork.Value;
        if (dto.TlsSkipVerify is not null) channel.TlsSkipVerify = dto.TlsSkipVerify.Value;
        if (dto.Enabled is not null) channel.Enabled = dto.Enabled.Value;

        // Validated against the merged row, not the dto: host and security can
        // arrive in separate requests.
        ValidateConnection(channel.Host, channel.Port, channel.Security);
        ValidateIdentity(channel.FromAddress, channel.ReplyTo);
        ValidateAuth(
            channel.Username,
            // On update the password may be untouched — treat a stored secret
            // as "a password is present" for the plaintext-auth check.
            dto.Password ?? (channel.EncryptedPassword is { Length: > 0 } ? "<stored>" : null),
            channel.Security,
            channel.AllowPrivateNetwork);

        if (dto.IsDefault is not null && dto.IsDefault.Value != channel.IsDefault)
        {
            if (dto.IsDefault.Value) await DemoteExistingDefaultsAsync(exceptId: id);
            channel.IsDefault = dto.IsDefault.Value;
        }

        channel.UpdatedAt = DateTime.UtcNow;
        await _channels.SaveChangesAsync();

        await _auditLog.LogAsync("email_channel", channel.EmailChannelId, "update",
            before: auditBefore,
            after: new
            {
                channel.Name,
                channel.Provider,
                channel.Host,
                channel.Port,
                channel.Security,
                channel.Username,
                from_address = channel.FromAddress,
                allow_private_network = channel.AllowPrivateNetwork,
                tls_skip_verify = channel.TlsSkipVerify,
                is_default = channel.IsDefault,
                channel.Enabled,
                password_rotated = dto.Password is not null,
            });

        return ToResponse(channel);
    }

    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var channel = await _channels.GetByIdAsync(id, tracking: true)
            ?? throw new NotFoundException("email channel", id);
        channel.IsActive = false;
        channel.Enabled = false;
        // A soft-deleted row must not stay the fallback for email_send steps.
        channel.IsDefault = false;
        channel.UpdatedAt = DateTime.UtcNow;
        await _channels.SaveChangesAsync();

        await _auditLog.LogAsync("email_channel", channel.EmailChannelId, "delete",
            before: ChannelAudit(channel));

        return new NoContentResult();
    }

    public async Task<ActionResult<TestEmailChannelResponse>> TestAsync(
        Guid id, TestEmailChannelRequest dto, CancellationToken ct = default)
    {
        var channel = await _channels.GetByIdAsync(id, tracking: true)
            ?? throw new NotFoundException("email channel", id);

        if (Blank(dto.To) || !AddressShape.IsMatch(dto.To.Trim()))
            throw new ValidationException(
                "to must be a single valid email address", code: "invalid_recipient");

        var message = new EmailMessage
        {
            To = new[] { dto.To.Trim() },
            Subject = Blank(dto.Subject) ? "FlowWeaver test message" : dto.Subject!.Trim(),
            TextBody = Blank(dto.Body)
                ? $"This is a test message from the FlowWeaver email channel '{channel.Name}'."
                : dto.Body,
        };

        var result = await _sender.SendAsync(channel, message, ct);

        channel.LastSendAt = DateTime.UtcNow;
        channel.LastSendStatus = result.Ok ? "ok" : "failed";
        channel.UpdatedAt = DateTime.UtcNow;
        await _channels.SaveChangesAsync();

        // A live test reaches an external system with a stored credential —
        // audit the attempt and its outcome, never the recipient's message body.
        await _auditLog.LogAsync("email_channel", channel.EmailChannelId, "test",
            after: new { ok = result.Ok, host = channel.Host, error = result.Error });

        return new TestEmailChannelResponse
        {
            Ok = result.Ok,
            MessageId = result.MessageId,
            Detail = result.Detail,
            Error = result.Error,
            ElapsedMs = result.ElapsedMs,
        };
    }

    private async Task DemoteExistingDefaultsAsync(Guid? exceptId)
    {
        foreach (var other in await _channels.ListDefaultsTrackedAsync())
        {
            if (exceptId is not null && other.EmailChannelId == exceptId) continue;
            other.IsDefault = false;
            other.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static void ValidateConnection(string host, int port, string security)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ValidationException("host is required", code: "host_required");
        if (port is < 1 or > 65535)
            throw new ValidationException("port must be between 1 and 65535", code: "invalid_port");
        if (!KnownSecurity.Contains(security))
            throw new ValidationException(
                $"security must be one of: {string.Join(", ", KnownSecurity)}",
                code: "invalid_security");
    }

    private static void ValidateIdentity(string? fromAddress, string? replyTo)
    {
        if (string.IsNullOrWhiteSpace(fromAddress))
            throw new ValidationException("from_address is required", code: "from_address_required");
        if (!AddressShape.IsMatch(fromAddress.Trim()))
            throw new ValidationException(
                "from_address must be a valid email address", code: "invalid_from_address");
        if (!Blank(replyTo) && !AddressShape.IsMatch(replyTo!.Trim()))
            throw new ValidationException(
                "reply_to must be a valid email address", code: "invalid_reply_to");
    }

    private static void ValidateAuth(
        string? username, string? password, string security, bool allowPrivateNetwork)
    {
        if (Blank(username)) return;
        if (Blank(password))
            throw new ValidationException(
                "password is required when username is set", code: "password_required");

        // Refuse to hand a credential to an unencrypted connection reachable on
        // the public internet. An operator who really has a plaintext relay on
        // their own LAN opts in by setting allow_private_network.
        if (string.Equals(security, EmailChannel.SecurityNone, StringComparison.OrdinalIgnoreCase)
            && !allowPrivateNetwork)
        {
            throw new ValidationException(
                "security 'none' sends the SMTP password in the clear. Use starttls or ssl, "
                + "or set allow_private_network for an internal relay.",
                code: "plaintext_auth_refused");
        }
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private static EmailChannelResponse ToResponse(EmailChannel c) => new()
    {
        EmailChannelId = c.EmailChannelId,
        Name = c.Name,
        Provider = c.Provider,
        Host = c.Host,
        Port = c.Port,
        Security = c.Security,
        Username = c.Username,
        HasPassword = c.EncryptedPassword is { Length: > 0 },
        FromAddress = c.FromAddress,
        FromName = c.FromName,
        ReplyTo = c.ReplyTo,
        AllowPrivateNetwork = c.AllowPrivateNetwork,
        TlsSkipVerify = c.TlsSkipVerify,
        IsDefault = c.IsDefault,
        Enabled = c.Enabled,
        LastSendAt = c.LastSendAt,
        LastSendStatus = c.LastSendStatus,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}
