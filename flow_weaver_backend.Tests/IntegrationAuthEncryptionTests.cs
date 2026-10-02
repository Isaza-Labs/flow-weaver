using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace flow_weaver_backend.Tests;

// Integration.AuthConfig is encrypted at rest by a value converter on
// AppDbContext. Services read and write plaintext; the stored jsonb is an
// envelope.
//
// The stored value is checked through the model's converter rather than by
// reading the row from a second, cipher-less context: EF InMemory keeps the
// converter of whichever model first created its table, so a second context
// cannot look underneath it the way PostgreSQL would.
public class IntegrationAuthEncryptionTests
{
    private const string Secret = "nb-token-0123456789";

    private static readonly IntegrationAuthCipher Cipher = new(new EphemeralDataProtectionProvider());

    private static AppDbContext Context(IntegrationAuthCipher? cipher) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            cipher);

    private static ValueConverter AuthConverter(AppDbContext db) =>
        db.Model.FindEntityType(typeof(Integration))!.FindProperty(nameof(Integration.AuthConfig))!
            .GetValueConverter()!;

    private static JsonElement Auth() =>
        JsonDocument.Parse($$"""{"type":"bearer","token":"{{Secret}}"}""").RootElement;

    private static Integration NewIntegration(JsonElement auth) => new()
    {
        IntegrationId = Guid.NewGuid(), Name = "netbox", Type = "rest",
        BaseURL = "https://netbox.example.com", AuthConfig = auth, IsActive = true,
    };

    [Fact]
    public void Cipher_round_trips_and_hides_the_plaintext()
    {
        var stored = Cipher.Protect(Auth());

        Assert.DoesNotContain(Secret, stored);
        Assert.True(IntegrationAuthCipher.IsEnvelope(stored));
        Assert.Equal(Secret, Cipher.Unprotect(stored).GetProperty("token").GetString());
    }

    [Fact]
    public void Cipher_passes_legacy_plaintext_and_null_through()
    {
        var legacy = Auth().GetRawText();

        Assert.False(IntegrationAuthCipher.IsEnvelope(legacy));
        Assert.Equal(Secret, Cipher.Unprotect(legacy).GetProperty("token").GetString());
        Assert.Equal("null", Cipher.Protect(default));
        Assert.Equal(JsonValueKind.Null, Cipher.Unprotect("null").ValueKind);
    }

    [Fact]
    public void Context_with_a_cipher_writes_an_envelope_and_reads_legacy_plaintext()
    {
        using var db = Context(Cipher);
        var converter = AuthConverter(db);

        var stored = (string)converter.ConvertToProvider(Auth())!;
        Assert.DoesNotContain(Secret, stored);
        Assert.True(IntegrationAuthCipher.IsEnvelope(stored));

        var fromEnvelope = (JsonElement)converter.ConvertFromProvider(stored)!;
        Assert.Equal(Secret, fromEnvelope.GetProperty("token").GetString());

        var fromLegacy = (JsonElement)converter.ConvertFromProvider(Auth().GetRawText())!;
        Assert.Equal(Secret, fromLegacy.GetProperty("token").GetString());
    }

    [Fact]
    public void Context_without_a_cipher_stores_the_config_as_is()
    {
        using var db = Context(cipher: null);

        var stored = (string)AuthConverter(db).ConvertToProvider(Auth())!;
        Assert.Contains(Secret, stored);
    }

    [Fact]
    public async Task Services_read_back_plaintext_through_the_context()
    {
        await using var db = Context(Cipher);
        db.Integrations.Add(NewIntegration(Auth()));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var read = await db.Integrations.SingleAsync();
        Assert.Equal(Secret, read.AuthConfig.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Backfill_re_saves_rows_and_they_stay_readable()
    {
        await using var db = Context(Cipher);
        db.Integrations.Add(NewIntegration(Auth()));
        db.Integrations.Add(NewIntegration(default));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Only the row holding an object config is re-saved; null configs hold no secret.
        Assert.Equal(1, await IntegrationAuthBackfill.EncryptPlaintextAsync(db));
        db.ChangeTracker.Clear();

        var read = await db.Integrations.Where(i => i.Name == "netbox").ToListAsync();
        Assert.Contains(read, i => i.AuthConfig.ValueKind == JsonValueKind.Object
                                   && i.AuthConfig.GetProperty("token").GetString() == Secret);
    }

    [Fact]
    public async Task Backfill_without_a_cipher_does_nothing()
    {
        await using var db = Context(cipher: null);
        db.Integrations.Add(NewIntegration(Auth()));
        await db.SaveChangesAsync();

        Assert.Equal(0, await IntegrationAuthBackfill.EncryptPlaintextAsync(db));
    }
}
