using Amazon;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;

namespace flow_weaver_backend.Services.Security.Kms;

// Wraps the DataProtection keyring with AWS KMS. The KMS key
// itself is identified by `DataProtection:Aws:KeyId` (a key ARN, alias
// ARN, or alias name). AWS credentials follow the standard SDK chain:
// environment variables, instance profile, shared credentials file.
//
// We use Encrypt/Decrypt with the symmetric KMS key — KMS produces an
// envelope ciphertext that contains the key version implicitly, so
// rotating the KMS root rolls forward without invalidating older
// keyring entries (KMS auto-selects the version that wrote the
// ciphertext during Decrypt).
//
// Cost: the keyring rotates on its own DataProtection schedule
// (~90 days), so KMS Encrypt is called once per rotation. Decrypt is
// called on every boot per stored key. With the default 4 stored keys
// that is a handful of KMS calls per restart — negligible.
public sealed class AwsKmsKeyWrapper : IKmsKeyWrapper, IDisposable
{
    private readonly IAmazonKeyManagementService _client;
    private readonly string _keyId;
    private readonly bool _ownsClient;

    public string ProviderName => "aws-kms";

    public AwsKmsKeyWrapper(IConfiguration configuration)
    {
        _keyId = configuration["DataProtection:Aws:KeyId"]
            ?? throw new InvalidOperationException(
                "DataProtection:KmsProvider=aws-kms requires DataProtection:Aws:KeyId.");
        var region = configuration["DataProtection:Aws:Region"];
        var awsConfig = new AmazonKeyManagementServiceConfig();
        if (!string.IsNullOrWhiteSpace(region))
        {
            awsConfig.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }
        _client = new AmazonKeyManagementServiceClient(awsConfig);
        _ownsClient = true;
    }

    // Test/DI-friendly overload: caller supplies the client (e.g. a
    // mock or one configured with a non-default credentials chain).
    public AwsKmsKeyWrapper(IAmazonKeyManagementService client, string keyId)
    {
        _client = client;
        _keyId = keyId;
        _ownsClient = false;
    }

    public async Task<byte[]> WrapAsync(byte[] plaintext, CancellationToken ct)
    {
        using var ms = new MemoryStream(plaintext);
        var resp = await _client.EncryptAsync(new EncryptRequest
        {
            KeyId = _keyId,
            Plaintext = ms,
            EncryptionAlgorithm = EncryptionAlgorithmSpec.SYMMETRIC_DEFAULT,
        }, ct);
        return resp.CiphertextBlob.ToArray();
    }

    public async Task<byte[]> UnwrapAsync(byte[] ciphertext, CancellationToken ct)
    {
        using var ms = new MemoryStream(ciphertext);
        var resp = await _client.DecryptAsync(new DecryptRequest
        {
            CiphertextBlob = ms,
            // Specifying KeyId on Decrypt is optional but tightens the
            // policy: KMS rejects ciphertexts that were encrypted under
            // a different key alias, even if our IAM allows both.
            KeyId = _keyId,
            EncryptionAlgorithm = EncryptionAlgorithmSpec.SYMMETRIC_DEFAULT,
        }, ct);
        return resp.Plaintext.ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
