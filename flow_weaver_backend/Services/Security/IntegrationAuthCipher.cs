using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace flow_weaver_backend.Services.Security;

/// <summary>
/// Encrypts <c>Integration.AuthConfig</c> at rest with the Data Protection key
/// ring that already protects credentials.
/// </summary>
/// <remarks>
/// <para>
/// The column stays jsonb: the stored value is an envelope object
/// <c>{"$enc":"v1","ct":"&lt;base64&gt;"}</c> whose ciphertext is the protected
/// raw JSON of the auth config. AppDbContext applies this as a value converter,
/// so every service reads and writes plaintext <see cref="JsonElement"/>s and
/// nothing above the context changes.
/// </para>
/// <para>
/// A stored value that is not an envelope is legacy plaintext and is returned
/// unchanged; <see cref="IntegrationAuthBackfill"/> re-saves those rows at
/// startup so they end up encrypted. Null / undefined configs are stored as
/// JSON null, which holds no secret.
/// </para>
/// </remarks>
public sealed class IntegrationAuthCipher
{
    private const string Purpose = "flow-weaver.integration-auth.v1";
    private const string EnvelopeKey = "$enc";
    private const string EnvelopeVersion = "v1";
    private const string CiphertextKey = "ct";

    private readonly IDataProtector _protector;

    public IntegrationAuthCipher(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    /// <summary>Provider-side value for the jsonb column.</summary>
    public string Protect(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return "null";

        var ciphertext = _protector.Protect(Encoding.UTF8.GetBytes(value.GetRawText()));
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [EnvelopeKey] = EnvelopeVersion,
            [CiphertextKey] = Convert.ToBase64String(ciphertext),
        });
    }

    /// <summary>Model-side value: decrypts an envelope, passes legacy plaintext through.</summary>
    public JsonElement Unprotect(string? stored)
    {
        var element = Parse(stored);
        if (!TryGetCiphertext(element, out var ciphertext))
            return element;

        var plaintext = _protector.Unprotect(Convert.FromBase64String(ciphertext));
        return Parse(Encoding.UTF8.GetString(plaintext));
    }

    /// <summary>True when the stored jsonb text is an encryption envelope.</summary>
    public static bool IsEnvelope(string? stored) => TryGetCiphertext(Parse(stored), out _);

    private static JsonElement Parse(string? json) =>
        JsonDocument.Parse(string.IsNullOrEmpty(json) ? "null" : json, default).RootElement;

    private static bool TryGetCiphertext(JsonElement element, out string ciphertext)
    {
        ciphertext = string.Empty;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty(EnvelopeKey, out var version)
            || version.ValueKind != JsonValueKind.String
            || version.GetString() != EnvelopeVersion) return false;
        if (!element.TryGetProperty(CiphertextKey, out var ct) || ct.ValueKind != JsonValueKind.String)
            return false;
        ciphertext = ct.GetString()!;
        return true;
    }
}
