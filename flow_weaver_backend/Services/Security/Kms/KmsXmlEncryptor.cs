using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace flow_weaver_backend.Services.Security.Kms;

// S13.4: bridges IKmsKeyWrapper into ASP.NET Core's DataProtection
// machinery. DataProtection calls Encrypt() with a `<descriptor>`
// XElement when persisting a new key; we serialise it, hand the bytes
// to the wrapper, and produce an XElement that holds the wrapped
// ciphertext base64-encoded.
//
// On read, KmsXmlDecryptor pairs with this encoder by detecting the
// custom decryptor type attribute and reverses the round-trip.
public sealed class KmsXmlEncryptor : IXmlEncryptor
{
    public const string ElementName = "kmsEncryptedKey";
    public const string ProviderAttribute = "provider";

    private readonly IKmsKeyWrapper _wrapper;

    public KmsXmlEncryptor(IKmsKeyWrapper wrapper)
    {
        _wrapper = wrapper;
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString());
        // .GetAwaiter().GetResult() — DataProtection's IXmlEncryptor is
        // synchronous; the wrap is rare (every ~90 days) so the blocking
        // call is acceptable. The KMS SDK only exposes async, hence the
        // bridge.
        var wrapped = _wrapper.WrapAsync(plaintextBytes, CancellationToken.None)
            .GetAwaiter().GetResult();

        var element = new XElement(ElementName,
            new XAttribute(ProviderAttribute, _wrapper.ProviderName),
            new XElement("ciphertext", Convert.ToBase64String(wrapped)));

        return new EncryptedXmlInfo(element, typeof(KmsXmlDecryptor));
    }
}
