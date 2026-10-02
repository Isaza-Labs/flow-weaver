namespace flow_weaver_backend.Services.Security.Kms;

// Passthrough implementation. Used when DataProtection:KmsProvider is
// unset or "filesystem" — the keyring lives in plaintext on the keyring
// volume. Behavior matches deployments without KMS so flipping the flag
// off produces no behavioural change.
public sealed class NoOpKmsKeyWrapper : IKmsKeyWrapper
{
    public string ProviderName => "none";

    public Task<byte[]> WrapAsync(byte[] plaintext, CancellationToken ct)
        => Task.FromResult(plaintext);

    public Task<byte[]> UnwrapAsync(byte[] ciphertext, CancellationToken ct)
        => Task.FromResult(ciphertext);
}
