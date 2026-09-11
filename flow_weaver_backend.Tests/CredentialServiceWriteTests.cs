using System.Security.Cryptography;
using System.Text;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Credential;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using CredentialModel = flow_weaver_backend.Models.Credential;

namespace flow_weaver_backend.Tests;

// SSH credentials. Two things carry the weight here: no secret material may
// appear in a response, and a credential that claims `auth_method=key` must
// actually carry a usable key — otherwise every run against every device in
// that credential's inventory fails at connect time, long after the admin
// saved the form.
public class CredentialServiceWriteTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public CredentialService Build() => new(
            new RepositoryBase<CredentialModel>(Db),
            new FakeCrypto(),
            new FakeUser(),
            new FakeAudit(), NullLogger<CredentialService>.Instance);

        public Guid Seed(
            string authMethod = "password", string? key = null,
            string? password = "hunter2", bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Credentials.Add(new CredentialModel
            {
                CredentialId = id,
                Name = "lab-ssh",
                Type = "ssh",
                Username = "admin",
                AuthMethod = authMethod,
                EncryptedPassword = password is null ? null : Encoding.UTF8.GetBytes(password),
                EncryptedPrivateKey = key is null ? null : Encoding.UTF8.GetBytes(key),
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    // A real, well-formed PEM — the validator decodes the base64 body, so a
    // hand-written placeholder would not do.
    private static string ValidPem()
    {
        using var rsa = RSA.Create(2048);
        return new string(PemEncoding.Write("PRIVATE KEY", rsa.ExportPkcs8PrivateKey()));
    }

    private static string ErrorOf<T>(ActionResult<T> result)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result.Result).Value!;
        return value.GetType().GetProperty("error")?.GetValue(value)?.ToString() ?? "";
    }

    private static CredentialResponse Body(ActionResult<CredentialResponse> result)
        => result.Value ?? Assert.IsType<CredentialResponse>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    // ─── Create ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "ssh", "name is required")]
    [InlineData("   ", "ssh", "name is required")]
    [InlineData("lab", "", "type is required")]
    public async Task Create_RequiresNameAndType(string name, string type, string expected)
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(new CreateCredential { Name = name, Type = type });

        Assert.Equal(expected, ErrorOf(result));
        Assert.Empty(f.Db.Credentials);
    }

    [Fact]
    public async Task Create_PersistsAnEncryptedCredential()
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(new CreateCredential
        {
            Name = "lab-ssh", Type = "ssh", Username = "admin", Password = "hunter2",
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var saved = Assert.Single(f.Db.Credentials);
        Assert.NotNull(saved.EncryptedPassword);
        Assert.True(saved.IsActive);
    }

    // The response type has no slot that could carry secret material — that
    // is the guarantee, not a runtime check. `HasPrivateKey` is deliberately
    // present: it is a bool the UI needs to render "key uploaded", and a bool
    // can't leak the key.
    [Fact]
    public void TheResponseTypeExposesNoSecretMaterial()
    {
        var leaky = typeof(CredentialResponse).GetProperties()
            .Where(p => p.PropertyType != typeof(bool))
            .Where(p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(leaky);
        Assert.Equal(typeof(bool), typeof(CredentialResponse).GetProperty("HasPrivateKey")!.PropertyType);
    }

    // An absent or unrecognised auth method means password — the historical
    // default, so old rows and old clients keep working.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kerberos")]
    public async Task Create_AnUnrecognisedAuthMethodBecomesPassword(string? raw)
    {
        using var f = new Fixture();

        await f.Build().PostAsync(new CreateCredential
        {
            Name = "lab", Type = "ssh", AuthMethod = raw, Password = "x",
        });

        Assert.Equal(CredentialModel.AuthMethodPassword, f.Db.Credentials.Single().AuthMethod);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("KEY")]
    [InlineData("  Key  ")]
    public async Task Create_TheKeyAuthMethodIsNormalised(string raw)
    {
        using var f = new Fixture();

        await f.Build().PostAsync(new CreateCredential
        {
            Name = "lab", Type = "ssh", AuthMethod = raw, PrivateKey = ValidPem(),
        });

        Assert.Equal(CredentialModel.AuthMethodKey, f.Db.Credentials.Single().AuthMethod);
    }

    // Saving `auth_method=key` with no key would fail at connect time on
    // every device — catch it at save time instead.
    [Fact]
    public async Task Create_KeyAuthWithoutAKeyIsRejected()
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(new CreateCredential
        {
            Name = "lab", Type = "ssh", AuthMethod = "key",
        });

        Assert.Contains("requires private_key", ErrorOf(result));
        Assert.Empty(f.Db.Credentials);
    }

    [Theory]
    [InlineData("not a pem at all")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nnot base64!!\n-----END PRIVATE KEY-----")]
    public async Task Create_AMalformedPemIsRejectedWithACleanMessage(string pem)
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(new CreateCredential
        {
            Name = "lab", Type = "ssh", AuthMethod = "key", PrivateKey = pem,
        });

        Assert.Contains("private_key", ErrorOf(result));
        Assert.Empty(f.Db.Credentials);
    }

    // A certificate pasted into the key field is well-formed PEM but the
    // wrong kind, so the label is checked too.
    [Fact]
    public async Task Create_APemWithTheWrongLabelIsRejected()
    {
        using var f = new Fixture();
        using var rsa = RSA.Create(2048);
        var certPem = new string(PemEncoding.Write("CERTIFICATE", rsa.ExportRSAPublicKey()));

        var result = await f.Build().PostAsync(new CreateCredential
        {
            Name = "lab", Type = "ssh", AuthMethod = "key", PrivateKey = certPem,
        });

        Assert.Contains("PRIVATE KEY", ErrorOf(result));
    }

    [Fact]
    public async Task Create_AValidKeyCredentialIsAccepted()
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(new CreateCredential
        {
            Name = "lab", Type = "ssh", AuthMethod = "key",
            PrivateKey = ValidPem(), KeyPassphrase = "phrase",
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var saved = Assert.Single(f.Db.Credentials);
        Assert.NotNull(saved.EncryptedPrivateKey);
        Assert.NotNull(saved.EncryptedKeyPassphrase);
    }

    // A password credential needs no key material at all.
    [Fact]
    public async Task Create_PasswordAuthNeedsNoKey()
    {
        using var f = new Fixture();

        var result = await f.Build().PostAsync(new CreateCredential
        {
            Name = "lab", Type = "ssh", AuthMethod = "password", Password = "hunter2",
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // ─── Update ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_AnUnknownIdIs404()
    {
        using var f = new Fixture();

        var result = await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateCredential { Name = "x" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // PATCH semantics: an omitted secret keeps the stored ciphertext, so
    // renaming a credential can't wipe its password.
    [Fact]
    public async Task Update_OmittedSecretsKeepTheStoredCiphertext()
    {
        using var f = new Fixture();
        var id = f.Seed(password: "original");

        await f.Build().UpdateAsync(id, new UpdateCredential { Name = "renamed" });

        var saved = f.Db.Credentials.Single();
        Assert.Equal("renamed", saved.Name);
        Assert.Equal("original", Encoding.UTF8.GetString(saved.EncryptedPassword!));
    }

    [Fact]
    public async Task Update_ASuppliedPasswordRotatesTheCiphertext()
    {
        using var f = new Fixture();
        var id = f.Seed(password: "original");

        await f.Build().UpdateAsync(id, new UpdateCredential { Password = "rotated" });

        Assert.Equal("rotated", Encoding.UTF8.GetString(f.Db.Credentials.Single().EncryptedPassword!));
    }

    // The final-state re-validation: flipping to key auth without uploading
    // a key in the same request must be caught, even though each field on
    // its own looks fine.
    [Fact]
    public async Task Update_FlippingToKeyAuthWithoutAKeyIsRejected()
    {
        using var f = new Fixture();
        var id = f.Seed(authMethod: "password");

        var result = await f.Build().UpdateAsync(id, new UpdateCredential { AuthMethod = "key" });

        Assert.Contains("requires private_key", ErrorOf(result));
    }

    // Flipping to key auth is fine when the row ALREADY carries a key —
    // the validator falls back to the stored one.
    [Fact]
    public async Task Update_FlippingToKeyAuthIsAllowedWhenAKeyIsAlreadyStored()
    {
        using var f = new Fixture();
        var id = f.Seed(authMethod: "password", key: ValidPem());

        var result = await f.Build().UpdateAsync(id, new UpdateCredential { AuthMethod = "key" });

        Assert.NotNull(Body(result));
        Assert.Equal(CredentialModel.AuthMethodKey, f.Db.Credentials.Single().AuthMethod);
    }

    [Fact]
    public async Task Update_ReplacingTheKeyWithAMalformedOneIsRejected()
    {
        using var f = new Fixture();
        var id = f.Seed(authMethod: "key", key: ValidPem());

        var result = await f.Build().UpdateAsync(
            id, new UpdateCredential { PrivateKey = "garbage" });

        Assert.Contains("private_key", ErrorOf(result));
    }

    // Downgrading to password auth is always fine, even from a key
    // credential — de-escalation needs no key.
    [Fact]
    public async Task Update_DowngradingToPasswordAuthIsAccepted()
    {
        using var f = new Fixture();
        var id = f.Seed(authMethod: "key", key: ValidPem());

        var result = await f.Build().UpdateAsync(
            id, new UpdateCredential { AuthMethod = "password", Password = "hunter2" });

        Assert.NotNull(Body(result));
        Assert.Equal(CredentialModel.AuthMethodPassword, f.Db.Credentials.Single().AuthMethod);
    }

    // ─── Read / delete ──────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ReturnsTheCredentialWithoutSecrets()
    {
        using var f = new Fixture();
        var id = f.Seed();

        var body = Body(await f.Build().GetByIdAsync(id));

        Assert.Equal(id, body.CredentialId);
        Assert.Equal("lab-ssh", body.Name);
        Assert.Equal("admin", body.Username);
    }

    [Fact]
    public async Task GetById_AnUnknownIdIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().GetByIdAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Get_ListsTheStoredCredentials()
    {
        using var f = new Fixture();
        f.Seed();
        f.Seed();

        var result = await f.Build().GetAsync();

        var page = Assert.IsType<ListResponse<CredentialResponse>>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Data.Count);
    }

    [Fact]
    public async Task Delete_SoftDeletesTheRow()
    {
        using var f = new Fixture();
        var id = f.Seed();

        await f.Build().DeleteAsync(id);

        Assert.False(f.Db.Credentials.Single().IsActive);
    }

    [Fact]
    public async Task Delete_AnUnknownIdIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().DeleteAsync(Guid.NewGuid())).Result);
    }
}

