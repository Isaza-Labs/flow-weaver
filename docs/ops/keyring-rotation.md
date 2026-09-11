# DataProtection keyring — rotation and KMS plan

The ASP.NET DataProtection keyring at `/app/keyring` (mounted as the
`backend_keyring` Docker volume) protects every encrypted column in the
database, including:

- `Credential.EncryptedSecret`
- `Secret.EncryptedValue`
- `Integration.AuthPayload`
- `WorkflowTrigger.WebhookSecret`

Losing the keyring means losing access to every credential and secret
stored at-rest. Compromising the keyring grants the attacker the same
access. This document covers both halves: how to rotate it on schedule,
and how we plan to migrate to a KMS-managed root key.

## Phase 1 — Filesystem keyring rotation (today)

ASP.NET DataProtection rotates keys automatically every 90 days by
default. It does **not** delete old keys, so previously-encrypted data
remains decryptable. We accept the default lifetime and add operational
hygiene:

| Action | Cadence | Owner |
|---|---|---|
| Snapshot `backend_keyring` volume | Same window as Postgres full backup | Operations |
| Verify new key generation | Quarterly | Operations |
| Cycle the keyring on personnel turnover (admin leaves) | Event-driven | Security |

### Snapshot procedure

```bash
docker run --rm \
  -v flow-weaver_backend_keyring:/keyring:ro \
  -v "$(pwd)":/out \
  alpine tar -czf /out/keyring-$(date -u +%Y%m%dT%H%M%SZ).tar.gz -C /keyring .
```

Store the resulting tarball in the same encrypted bucket as the Postgres
backups. Without this snapshot, a database restore is useless — the
encrypted columns cannot be decrypted.

### Forced rotation (compromise / personnel change)

1. Stop backend and worker.
2. Move existing keyring aside: `mv /keyring /keyring-old-$stamp`.
3. Start backend; it generates a fresh key.
4. Run the credential re-encryption job (see `Services/Credential/CredentialReEncryptionService` —
   tracked under [N-4 follow-up], not yet implemented; until then, treat
   forced rotation as a destructive event that requires re-entering all
   credentials).
5. Archive `/keyring-old-$stamp` under sealed storage for as long as the
   compliance retention window requires.

> Open work: build a re-encryption service that reads ciphertext under
> the old protector and writes ciphertext under the new one. Tracked as
> follow-up to S13.4 phase 1.

## Phase 2 — KMS-backed root key (implemented for AWS, pluggable)

### Activation

Set the configuration:

```jsonc
{
  "DataProtection": {
    "KeyRingPath": "/app/keyring",
    "KmsProvider": "aws-kms",
    "Aws": {
      "KeyId": "alias/flow-weaver-keyring",   // or full ARN
      "Region": "us-east-1"                   // optional; SDK chain otherwise
    }
  }
}
```

AWS credentials follow the standard SDK chain: env vars
(`AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY`), instance profile,
shared credentials file, or IRSA on EKS. The IAM policy on the role
needs `kms:Encrypt` and `kms:Decrypt` for the configured key ARN — and
nothing else (`*` is unsafe).

### What happens at boot

- `IKmsKeyWrapper` resolves to `AwsKmsKeyWrapper`. The AWS client is
  built lazily; first KMS call is on the first key persistence (or on
  read of an existing wrapped key).
- `KmsXmlEncryptor` is registered as the DataProtection
  `KeyManagementOptions.XmlEncryptor`.
- New key descriptors are wrapped with KMS Encrypt before being written
  to disk; existing **plaintext** descriptors are still readable
  (they predate the wrap and live on the volume as XML).

### Migration from plaintext to wrapped

Plaintext descriptors stay readable forever — DataProtection ignores
the encryptor on read. A clean migration:

1. Snapshot the keyring volume (Phase 1 backup).
2. Flip `DataProtection:KmsProvider` to `aws-kms` + `Aws:KeyId`.
3. Restart. New keys land wrapped; old plaintext keys keep working.
4. Wait for one rotation cycle (default 90 days). After that every
   active descriptor is wrapped, and any plaintext key has expired.
5. Optional: rotate the Phase 1 snapshot off the encrypted backups
   bucket since the original plaintext is no longer load-bearing.

### Switching providers (Azure / Vault later)

`IKmsKeyWrapper` is the entire surface a new provider needs to
implement. The wiring in `Program.cs` switches on
`DataProtection:KmsProvider`; add a `case "azure-key-vault":` branch
that registers an `AzureKeyVaultKmsKeyWrapper` and add the SDK package
to the csproj.

## Phase 3 — operational concerns (open)

- **Multi-region failover**: KMS keys are region-scoped. For multi-AZ
  HA inside one region the SDK handles it transparently. Cross-region
  failover requires a [multi-region KMS key](https://docs.aws.amazon.com/kms/latest/developerguide/multi-region-keys-overview.html);
  enable when the deployment topology demands it.
- **Key rotation cadence on the KMS root**: AWS rotates symmetric KMS
  keys yearly by default. KMS Decrypt picks the version that wrote each
  ciphertext, so rotation is transparent to FlowWeaver.
- **Break-glass when KMS is unreachable**: every boot calls KMS Decrypt
  for each persisted key. If the KMS endpoint is down, the keyring
  fails to unwrap and the platform refuses to start. Mitigation:
  cache decrypted keys in process memory after the first read (already
  the DataProtection default — keys are loaded on demand and cached
  for the process lifetime). For full break-glass, keep an offline
  copy of the unwrapped keyring in sealed storage (rotates annually).

## See also

- [`docs/ops/dr.md`](./dr.md) — Postgres DR.
- [`docs/ops/branch-protection.md`](./branch-protection.md) — CI gate.
