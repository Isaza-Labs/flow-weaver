using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace flow_weaver_backend.Services.Security.Kms;

// Counterpart to KmsXmlEncryptor. DataProtection looks at the
// `decryptorType` attribute the encryptor stamped on the wrapping
// element and instantiates the named decryptor here. We pull the
// wrapper from DI (registered as a singleton in Program.cs).
public sealed class KmsXmlDecryptor : IXmlDecryptor
{
    private readonly IKmsKeyWrapper _wrapper;

    public KmsXmlDecryptor(IServiceProvider services)
    {
        _wrapper = services.GetRequiredService<IKmsKeyWrapper>();
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        var ciphertextEl = encryptedElement.Element("ciphertext")
            ?? throw new InvalidOperationException(
                "kmsEncryptedKey element missing <ciphertext> child.");
        var ciphertext = Convert.FromBase64String(ciphertextEl.Value);
        var plaintext = _wrapper.UnwrapAsync(ciphertext, CancellationToken.None)
            .GetAwaiter().GetResult();
        var xml = System.Text.Encoding.UTF8.GetString(plaintext);
        return XElement.Parse(xml);
    }
}
