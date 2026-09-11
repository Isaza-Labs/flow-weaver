using System.Security.Cryptography;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using CredentialModel = flow_weaver_backend.Models.Credential;

namespace flow_weaver_backend.Services.Credential;

public class CredentialService : ICredential
{
    private readonly IRepository<CredentialModel> _credentials;
    private readonly ICredentialEncryptionService _crypto;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ILogger<CredentialService> _logger;

    public CredentialService(
        IRepository<CredentialModel> credentials,
        ICredentialEncryptionService crypto,
        ICurrentUser caller,
        IAuditLogger audit,
        ILogger<CredentialService> logger)
    {
        _credentials = credentials;
        _crypto = crypto;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<CredentialResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _credentials.CountAsync();
        var credentials = await _credentials.ListAsync(limit, offset);

        _logger.LogDebug(
            "credential.list.ok total={Total} returned={Returned}",
            total, credentials.Count);

        return new OkObjectResult(new ListResponse<CredentialResponse>
        {
            Data = credentials.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<CredentialResponse>> GetByIdAsync(Guid id)
    {
        var credential = await _credentials.GetByIdAsync(id);
        if (credential is null)
        {
            _logger.LogWarning("credential.get.not_found credential_id={CredentialId}", id);
            return new NotFoundObjectResult(new { error = "credential not found" });
        }

        return ToResponse(credential);
    }

    public async Task<ActionResult<CredentialResponse>> PostAsync(CreateCredential dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("credential.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.Type))
        {
            _logger.LogWarning("credential.create.validation_failed reason=type_required");
            return new BadRequestObjectResult(new { error = "type is required" });
        }

        var authMethod = NormalizeAuthMethod(dto.AuthMethod);
        var validationError = ValidateAuth(authMethod, dto.PrivateKey, dto.KeyPassphrase);
        if (validationError is not null)
        {
            _logger.LogWarning("credential.create.validation_failed reason=auth_invalid auth_method={AuthMethod}", authMethod);
            return new BadRequestObjectResult(new { error = validationError });
        }

        var now = DateTime.UtcNow;
        var credential = new CredentialModel
        {
            CredentialId = Guid.NewGuid(),
            Name = dto.Name,
            Type = dto.Type,
            Username = dto.Username,
            AuthMethod = authMethod,
            EncryptedPassword = _crypto.Encrypt(dto.Password),
            EncryptedPrivateKey = _crypto.Encrypt(dto.PrivateKey),
            EncryptedKeyPassphrase = _crypto.Encrypt(dto.KeyPassphrase),
            Extra = dto.Extra ?? default,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _credentials.Add(credential);
        try
        {
            await _credentials.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "credential.create.failed credential_name={CredentialName}", credential.Name);
            throw;
        }

        // Device credentials are the most sensitive rows in the product, and
        // until now creating, rotating or deleting one left no audit trail at
        // all. Secret material never enters the payload — the metadata is what
        // answers "who added an SSH key for prod, and when".
        await _audit.LogAsync("credential", credential.CredentialId, "create",
            after: new { credential.Name, credential.Type, credential.Username, credential.AuthMethod });

        _logger.LogInformation(
            "credential.create.ok credential_id={CredentialId} credential_name={CredentialName} auth_method={AuthMethod}",
            credential.CredentialId, credential.Name, credential.AuthMethod);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "Credential",
            routeValues: new { id = credential.CredentialId },
            value: ToResponse(credential));
    }

    public async Task<ActionResult<CredentialResponse>> UpdateAsync(Guid id, UpdateCredential dto)
    {
        var credential = await _credentials.GetByIdAsync(id);
        if (credential is null)
        {
            _logger.LogWarning("credential.update.not_found credential_id={CredentialId}", id);
            return new NotFoundObjectResult(new { error = "credential not found" });
        }

        // Pre-mutation snapshot (`credential` is tracked). Ciphertext is
        // never recorded; the *_rotated booleans below say a secret changed
        // without saying what it changed to.
        var auditBefore = new
        {
            credential.Name,
            credential.Type,
            credential.Username,
            credential.AuthMethod,
        };

        if (dto.Name is not null) credential.Name = dto.Name;
        if (dto.Type is not null) credential.Type = dto.Type;
        if (dto.Username is not null) credential.Username = dto.Username;
        if (dto.Password is not null) credential.EncryptedPassword = _crypto.Encrypt(dto.Password);
        if (dto.PrivateKey is not null) credential.EncryptedPrivateKey = _crypto.Encrypt(dto.PrivateKey);
        if (dto.KeyPassphrase is not null) credential.EncryptedKeyPassphrase = _crypto.Encrypt(dto.KeyPassphrase);
        if (dto.AuthMethod is not null) credential.AuthMethod = NormalizeAuthMethod(dto.AuthMethod);
        if (dto.Extra is not null) credential.Extra = dto.Extra.Value;

        // Re-validate the final state — catches cases where the admin
        // flipped AuthMethod to "key" without uploading a key in the
        // same request.
        var keyPemForValidation = dto.PrivateKey
            ?? (credential.EncryptedPrivateKey is { Length: > 0 }
                ? _crypto.Decrypt(credential.EncryptedPrivateKey)
                : null);
        var passphraseForValidation = dto.KeyPassphrase
            ?? (credential.EncryptedKeyPassphrase is { Length: > 0 }
                ? _crypto.Decrypt(credential.EncryptedKeyPassphrase)
                : null);
        var validationError = ValidateAuth(credential.AuthMethod, keyPemForValidation, passphraseForValidation);
        if (validationError is not null)
        {
            _logger.LogWarning(
                "credential.update.validation_failed credential_id={CredentialId} reason=auth_invalid auth_method={AuthMethod}",
                id, credential.AuthMethod);
            return new BadRequestObjectResult(new { error = validationError });
        }

        credential.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _credentials.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "credential.update.failed credential_id={CredentialId}", credential.CredentialId);
            throw;
        }

        await _audit.LogAsync("credential", credential.CredentialId, "update",
            before: auditBefore,
            after: new
            {
                credential.Name,
                credential.Type,
                credential.Username,
                credential.AuthMethod,
                password_rotated = dto.Password is not null,
                private_key_rotated = dto.PrivateKey is not null,
                key_passphrase_rotated = dto.KeyPassphrase is not null,
            });

        _logger.LogInformation("credential.update.ok credential_id={CredentialId}", credential.CredentialId);
        return ToResponse(credential);
    }

    public async Task<ActionResult<CredentialResponse>> DeleteAsync(Guid id)
    {
        var credential = await _credentials.GetByIdAsync(id);
        if (credential is null)
        {
            _logger.LogWarning("credential.delete.not_found credential_id={CredentialId}", id);
            return new NotFoundObjectResult(new { error = "credential not found" });
        }

        credential.IsActive = false;
        credential.UpdatedAt = DateTime.UtcNow;
        await _credentials.SaveChangesAsync();

        await _audit.LogAsync("credential", credential.CredentialId, "delete",
            before: new { credential.Name, credential.Type, credential.Username, credential.AuthMethod });

        _logger.LogInformation("credential.delete.ok credential_id={CredentialId}", credential.CredentialId);
        return ToResponse(credential);
    }

    private static string NormalizeAuthMethod(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return CredentialModel.AuthMethodPassword;
        var v = raw.Trim().ToLowerInvariant();
        return v == CredentialModel.AuthMethodKey
            ? CredentialModel.AuthMethodKey
            : CredentialModel.AuthMethodPassword;
    }

    // Returns an error message string when the combination is invalid,
    // null when the credential can be persisted.
    //
    // We used to parse the key deeply with Renci.SshNet's PrivateKeyFile
    // here — that coupled CredentialService to the SSH library and also
    // failed for key formats we actually accept at runtime (Ed25519 on
    // some Renci versions). Since the refactor to Netmiko the SSH path
    // runs in a Python subprocess; deep parsing with a matching library
    // (Paramiko / OpenSSL) would mean either a second pip spawn at
    // credential-save time (slow, ugly) or duplicating format detection
    // in C# (brittle). We instead validate SHAPE only:
    //
    //   - Looks like a PEM (BEGIN/END markers around a base64 body).
    //   - Label contains "PRIVATE KEY" (any flavour: OPENSSH, RSA, EC, ED).
    //   - Body decodes as base64.
    //
    // Deep validation (wrong passphrase, algorithm not supported by the
    // server, etc.) happens at connect time inside the Python runner and
    // surfaces as a clear SshHandler error — that's the honest bar.
    private static string? ValidateAuth(string authMethod, string? privateKeyPem, string? passphrase)
    {
        _ = passphrase; // passphrase correctness can only be checked by the SSH runner.

        if (!string.Equals(authMethod, CredentialModel.AuthMethodKey, StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.IsNullOrWhiteSpace(privateKeyPem))
            return "auth_method='key' requires private_key to be set";

        try
        {
            var fields = PemEncoding.Find(privateKeyPem);
            var labelSpan = privateKeyPem.AsSpan()[fields.Label];
            var label = labelSpan.ToString();
            if (!label.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
                return $"private_key PEM label must contain 'PRIVATE KEY' (got '{label}')";
            if (fields.DecodedDataLength <= 0)
                return "private_key PEM body is empty";
            // PemEncoding.Find verifies the base64 body decodes cleanly;
            // no extra TryFromBase64 call needed.
            return null;
        }
        catch (ArgumentException ex)
        {
            // PemEncoding.Find throws when BEGIN/END markers are missing
            // or malformed. Surface a clean message, not the framework one.
            return $"private_key is not a well-formed PEM: {ex.Message}";
        }
    }

    private static CredentialResponse ToResponse(CredentialModel c) => new()
    {
        CredentialId = c.CredentialId,
        Name = c.Name,
        Type = c.Type,
        Username = c.Username,
        AuthMethod = string.IsNullOrWhiteSpace(c.AuthMethod) ? "password" : c.AuthMethod,
        HasPrivateKey = c.EncryptedPrivateKey is { Length: > 0 },
        Extra = c.Extra,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}
