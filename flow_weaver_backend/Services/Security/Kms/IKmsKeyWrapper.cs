namespace flow_weaver_backend.Services.Security.Kms;

// Provider-neutral wrap/unwrap surface for the DataProtection
// keyring. The default `NoOpKmsKeyWrapper` is a passthrough — the
// keyring stays plaintext on disk, matching the default behavior. The
// AWS implementation (`AwsKmsKeyWrapper`) calls KMS Encrypt/Decrypt
// through the AWS SDK; an Azure / Vault provider would slot in here
// without touching call sites.
//
// Wrap is called when DataProtection persists a new key descriptor;
// Unwrap is called when the persisted XML is read back. Both are
// invoked through KmsXmlEncryptor / KmsXmlDecryptor.
public interface IKmsKeyWrapper
{
    // Identifies the active provider in logs and audit. "none",
    // "aws-kms", "azure-key-vault", "vault".
    string ProviderName { get; }

    // Returns the wrapped ciphertext for storage. Caller hands in
    // plaintext bytes (the serialised XML descriptor). The implementation
    // may add provider-specific framing; it must be reversible by
    // UnwrapAsync.
    Task<byte[]> WrapAsync(byte[] plaintext, CancellationToken ct);

    Task<byte[]> UnwrapAsync(byte[] ciphertext, CancellationToken ct);
}
