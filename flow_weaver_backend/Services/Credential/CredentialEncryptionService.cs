using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace flow_weaver_backend.Services;

public class CredentialEncryptionService : ICredentialEncryptionService
{
    private const string Purpose = "flow-weaver.credentials.v1";
    private readonly IDataProtector _protector;
    private readonly ILogger<CredentialEncryptionService> _logger;

    public CredentialEncryptionService(
        IDataProtectionProvider provider,
        ILogger<CredentialEncryptionService> logger)
    {
        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public byte[]? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return null;

        // Never log plaintext or ciphertext — only metadata.
        _logger.LogDebug("credential.encrypt.start plaintext_length={PlaintextLength}", plaintext.Length);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = _protector.Protect(bytes);
            _logger.LogDebug("credential.encrypt.ok ciphertext_length={CiphertextLength}", protectedBytes.Length);
            return protectedBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "credential.encrypt.failed");
            throw;
        }
    }

    public string? Decrypt(byte[]? ciphertext)
    {
        if (ciphertext is null || ciphertext.Length == 0)
            return null;

        // Never log plaintext — only metadata.
        _logger.LogDebug("credential.decrypt.start ciphertext_length={CiphertextLength}", ciphertext.Length);
        try
        {
            var bytes = _protector.Unprotect(ciphertext);
            var result = Encoding.UTF8.GetString(bytes);
            _logger.LogDebug("credential.decrypt.ok plaintext_length={PlaintextLength}", result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "credential.decrypt.failed ciphertext_length={CiphertextLength}", ciphertext.Length);
            throw;
        }
    }
}
