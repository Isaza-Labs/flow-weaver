# Licensing implementation guide

## Root controls

- `LICENSE`: canonical Apache 2.0 text.
- `NOTICE`: FlowWeaver and Isaza Labs LLC attribution.
- `LEGAL.md`: navigation.
- `REUSE.toml`: repository-wide Apache metadata for known first-party paths.
- `LICENSES/Apache-2.0.txt`: license text for REUSE/SPDX tooling.
- `THIRD-PARTY-NOTICES.md`: generated release attribution.

## File-level controls

Use SPDX headers for new source and documentation when practical. `REUSE.toml` covers formats that cannot safely carry comments and existing directories without per-file headers.

Third-party files must override the blanket rule through their own headers, an adjacent `.license` file or a closer `REUSE.toml`. Never delete upstream notices.

## Verification sequence

1. Run REUSE lint or an equivalent SPDX coverage check.
2. Generate dependency inventories from resolved NuGet, npm and Python/container inputs.
3. Compare detected licenses with approved component decisions.
4. Generate `THIRD-PARTY-NOTICES.md` and collect required full license texts.
5. Inspect the final source archive, binaries and images.
6. Record hashes and approval outside the public repository.

## Existing files

Do not mechanically prepend headers to generated files, lockfiles, third-party code or formats that reject comments. Use `REUSE.toml` or `.license` companions after confirming provenance.

