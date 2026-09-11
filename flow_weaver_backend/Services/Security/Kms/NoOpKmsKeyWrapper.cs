namespace flow_weaver_backend.Services.Security.Kms;

// Passthrough implementation. Used when DataProtection:KmsProvider is
// unset or "filesystem" — the keyring lives in plaintext on the keyring
// volume. Behavior matches pre-S13.4 deployments so flipping the flag
// off produces no behavioural change.
public sealed class NoOpKmsKeyWrapper : IKmsKeyWrapper
{
    public string ProviderName => "none";

    public Task<byte[]> WrapAsync(byte[] plaintext, CancellationToken ct)
        => Task.FromResult(plaintext);

    public Task<byte[]> UnwrapAsync(byte[] ciphertext, CancellationToken ct)
        => Task.FromResult(ciphertext);
}
