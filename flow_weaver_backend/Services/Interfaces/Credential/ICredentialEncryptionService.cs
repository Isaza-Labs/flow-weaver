namespace flow_weaver_backend.Services;

public interface ICredentialEncryptionService
{
    byte[]? Encrypt(string? plaintext);
    string? Decrypt(byte[]? ciphertext);
}
