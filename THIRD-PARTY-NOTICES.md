# Third-party notices

This file is the release-level control for third-party attribution in FlowWeaver.

**Source inventory:** CycloneDX 1.6 SBOM `flowweaver-sbom_2e0cc0b_2026-09-10.cdx.json`, produced on 10 September 2026 from the internal tree (commit `2e0cc0b`, branch `repository_pattern`) and validated against the official CycloneDX schema with no errors.

**How each layer was resolved:**

- **NuGet** — `dotnet restore` followed by `dotnet list package --include-transitive`. Each licence was read from the package's own `.nuspec`, not from a third-party table.
- **npm** — `frontend/package-lock.json` (lockfileVersion 3), with the licence resolved per package and exact version.
- **PyPI** — resolution of the ranges `deploy/Dockerfile` used to install, now pinned in `deploy/requirements.txt`.
- **Operating-system packages and base images** — declared from `deploy/Dockerfile` and `deploy/docker-compose.yml`.

> This inventory describes the internal tree on one date. **It is not yet the notice of a published release.** Before publishing, regenerate it from the lockfiles and images of that release and complete the items in section 4.

## 1. Summary

**517 components.** 466 reach the artifact that runs, 39 take part only in tests and 12 only in compilation. Only the first group is redistributed; the others are recorded because they belong to the supply chain and to any vulnerability analysis, not because their notice has to be reproduced.

| Origin | Components |
|---|---:|
| NuGet (.NET backend) | 128 |
| npm (frontend) | 359 |
| PyPI (Python runtime inside the backend image) | 18 |
| Operating-system packages (Debian, backend image) | 7 |
| Base images | 5 |
| **Total** | **517** |

### Declared licences

| Licence | Components |
|---|---:|
| MIT | 384 |
| Apache-2.0 | 46 |
| ISC | 36 |
| NOASSERTION | 13 |
| MPL-2.0 | 12 |
| BSD-3-Clause | 10 |
| MIT with Open Source Maintenance Fee (json-everything) | 3 |
| PostgreSQL | 2 |
| BSD-2-Clause | 2 |
| GPL-2.0-only WITH linking exception (libgit2) | 1 |
| MIT (Community) or commercial QuestPDF Professional/Enterprise licence | 1 |
| (MPL-2.0 OR Apache-2.0) | 1 |
| Unlicense | 1 |
| 0BSD | 1 |
| LGPL-2.1 | 1 |
| Apache-2.0 OR BSD-3-Clause | 1 |
| LGPL-2.1-or-later | 1 |
| MIT-0 | 1 |

`NOASSERTION` covers the operating-system packages and base images, whose exact versions and licences exist only inside a built image, plus one npm package whose registry metadata carries no licence field. All three cases are listed in section 4.

## 2. Components with specific obligations

These are the ones that a licence identifier does not settle. Each was verified by opening the licence file shipped inside the package itself, or the licence URL it declares.

| Component | Version | Declared licence | Stage |
|---|---|---|---|
| `Json.More.Net` | 3.0.1 | MIT with Open Source Maintenance Fee (json-everything) | runtime |
| `JsonPointer.Net` | 7.0.1 | MIT with Open Source Maintenance Fee (json-everything) | runtime |
| `JsonSchema.Net` | 9.2.0 | MIT with Open Source Maintenance Fee (json-everything) | runtime |
| `khroma` | 2.1.0 | NOASSERTION | runtime |
| `LibGit2Sharp.NativeBinaries` | 2.0.323 | GPL-2.0-only WITH linking exception (libgit2) | runtime |
| `paramiko` | 4.0.0 | LGPL-2.1 | runtime |
| `QuestPDF` | 2025.1.5 | MIT (Community) or commercial QuestPDF Professional/Enterprise licence | runtime |

**Json.More.Net 3.0.1** — The source is MIT, but the binary published on NuGet is offered under an Open Source Maintenance Fee agreement. Review the terms before redistributing the published binary.

**JsonPointer.Net 7.0.1** — The source is MIT, but the binary published on NuGet is offered under an Open Source Maintenance Fee agreement. Review the terms before redistributing the published binary.

**JsonSchema.Net 9.2.0** — The source is MIT, but the binary published on NuGet is offered under an Open Source Maintenance Fee agreement. Review the terms before redistributing the published binary.

**khroma 2.1.0** — The npm registry publishes no licence field for this version. Verify the licence in the upstream repository and record it here before a release.

**LibGit2Sharp.NativeBinaries 2.0.323** — Copyleft with a linking exception. Distributable alongside Apache-2.0 work precisely because of that exception, which must be reproduced verbatim next to the libgit2 copyright notice.

**paramiko 4.0.0** — Weak copyleft used at runtime inside the container. Installed unmodified as a separate package, so it does not affect the licence of FlowWeaver's own code, but it must remain declared.

**QuestPDF 2025.1.5** — Dual licence with a revenue threshold: MIT for the Community tier, paid Professional or Enterprise above it. Confirm which tier applies to Isaza Labs LLC before distributing, and record the answer.

None of the seven prevents distributing FlowWeaver under Apache 2.0. Four of them — QuestPDF and the three json-everything libraries — are **commercial** questions rather than licence-compatibility ones: the source is open and the fee attaches to commercial use or to the published binary. `LibGit2Sharp.NativeBinaries` is the only copyleft component, and it is distributable solely because of the libgit2 linking exception.

## 3. Full inventory

Stage: `runtime` is redistributed; `test` and `build` never leave the build process. ⚠️ marks a component covered by section 2.

### NuGet (.NET backend)

| Component | Version | Licence | Stage |
|---|---|---|---|
| `AWSSDK.Core` | 3.7.500 | Apache-2.0 | runtime |
| `AWSSDK.KeyManagementService` | 3.7.500 | Apache-2.0 | runtime |
| `Azure.Core` | 1.38.0 | MIT | runtime |
| `Azure.Identity` | 1.11.4 | MIT | runtime |
| `BouncyCastle.Cryptography` | 2.6.2 | MIT | runtime |
| `ClosedXML` | 0.105.0 | MIT | runtime |
| `ClosedXML.Parser` | 2.0.0 | MIT | runtime |
| `coverlet.collector` | 10.0.1 | MIT | test |
| `coverlet.MTP` | 10.0.1 | MIT | runtime |
| `Cronos` | 0.8.4 | MIT | runtime |
| `Dapper` | 2.1.66 | Apache-2.0 | runtime |
| `DocumentFormat.OpenXml` | 3.1.1 | MIT | runtime |
| `DocumentFormat.OpenXml.Framework` | 3.1.1 | MIT | runtime |
| `ExcelNumberFormat` | 1.1.0 | MIT | runtime |
| `Humanizer.Core` | 3.0.10 | MIT | runtime |
| `JmesPath.Net` | 1.1.0 | Apache-2.0 | runtime |
| `JmesPath.Net.Parser` | 1.1.0 | Apache-2.0 | runtime |
| `Json.More.Net` | 3.0.1 | MIT with Open Source Maintenance Fee (json-everything) ⚠️ | runtime |
| `JsonPointer.Net` | 7.0.1 | MIT with Open Source Maintenance Fee (json-everything) ⚠️ | runtime |
| `JsonSchema.Net` | 9.2.0 | MIT with Open Source Maintenance Fee (json-everything) ⚠️ | runtime |
| `JUnitXml.TestLogger` | 6.1.0 | MIT | test |
| `LibGit2Sharp` | 0.31.0 | MIT | runtime |
| `LibGit2Sharp.NativeBinaries` | 2.0.323 | GPL-2.0-only WITH linking exception (libgit2) ⚠️ | runtime |
| `MailKit` | 4.17.0 | MIT | runtime |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.6 | MIT | runtime |
| `Microsoft.AspNetCore.OpenApi` | 10.0.6 | MIT | runtime |
| `Microsoft.Azure.Relay` | 3.1.1 | MIT | runtime |
| `Microsoft.Bcl.AsyncInterfaces` | 1.1.1 | MIT | runtime |
| `Microsoft.Build.Framework` | 18.0.2 | MIT | runtime |
| `Microsoft.CodeAnalysis.Analyzers` | 3.11.0 | MIT | runtime |
| `Microsoft.CodeAnalysis.Common` | 5.0.0 | MIT | runtime |
| `Microsoft.CodeAnalysis.CSharp` | 5.0.0 | MIT | runtime |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | 5.0.0 | MIT | runtime |
| `Microsoft.CodeAnalysis.Workspaces.Common` | 5.0.0 | MIT | runtime |
| `Microsoft.CodeAnalysis.Workspaces.MSBuild` | 5.0.0 | MIT | runtime |
| `Microsoft.CodeCoverage` | 17.14.1 | MIT | test |
| `Microsoft.EntityFrameworkCore` | 10.0.6 | MIT | runtime |
| `Microsoft.EntityFrameworkCore` | 10.0.7 | MIT | test |
| `Microsoft.EntityFrameworkCore.Abstractions` | 10.0.7 | MIT | test |
| `Microsoft.EntityFrameworkCore.Abstractions` | 10.0.6 | MIT | runtime |
| `Microsoft.EntityFrameworkCore.Analyzers` | 10.0.6 | MIT | runtime |
| `Microsoft.EntityFrameworkCore.Analyzers` | 10.0.7 | MIT | test |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.6 | MIT | runtime |
| `Microsoft.EntityFrameworkCore.InMemory` | 10.0.7 | MIT | test |
| `Microsoft.EntityFrameworkCore.Relational` | 10.0.6 | MIT | runtime |
| `Microsoft.EntityFrameworkCore.Tools` | 10.0.6 | MIT | runtime |
| `Microsoft.Extensions.AI.Abstractions` | 10.5.2 | MIT | runtime |
| `Microsoft.Extensions.Caching.Abstractions` | 10.0.7 | MIT | test |
| `Microsoft.Extensions.Caching.Memory` | 10.0.7 | MIT | test |
| `Microsoft.Extensions.Configuration` | 10.0.8 | MIT | test |
| `Microsoft.Extensions.Configuration.Abstractions` | 10.0.8 | MIT | test |
| `Microsoft.Extensions.Configuration.Binder` | 9.0.0 | MIT | test |
| `Microsoft.Extensions.Configuration.FileExtensions` | 10.0.8 | MIT | test |
| `Microsoft.Extensions.Configuration.Json` | 10.0.8 | MIT | test |
| `Microsoft.Extensions.DependencyInjection` | 10.0.8 | MIT | test |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.8 | MIT | test |
| `Microsoft.Extensions.DependencyModel` | 10.0.6 | MIT | runtime |
| `Microsoft.Extensions.DependencyModel` | 9.0.0 | MIT | test |
| `Microsoft.Extensions.Diagnostics.Abstractions` | 10.0.6 | MIT | test |
| `Microsoft.Extensions.Diagnostics.HealthChecks` | 10.0.6 | MIT | test |
| `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` | 10.0.6 | MIT | test |
| `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` | 10.0.6 | MIT | runtime |
| `Microsoft.Extensions.FileProviders.Abstractions` | 10.0.8 | MIT | test |
| `Microsoft.Extensions.FileProviders.Physical` | 10.0.8 | MIT | test |
| `Microsoft.Extensions.FileSystemGlobbing` | 10.0.8 | MIT | test |
| `Microsoft.Extensions.Hosting.Abstractions` | 10.0.6 | MIT | test |
| `Microsoft.Extensions.Logging` | 10.0.7 | MIT | test |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.7 | MIT | test |
| `Microsoft.Extensions.Options` | 10.0.7 | MIT | test |
| `Microsoft.Extensions.Primitives` | 10.0.8 | MIT | test |
| `Microsoft.Identity.Client` | 4.61.3 | MIT | runtime |
| `Microsoft.Identity.Client.Extensions.Msal` | 4.61.3 | MIT | runtime |
| `Microsoft.IdentityModel.Abstractions` | 8.0.1 | MIT | runtime |
| `Microsoft.IdentityModel.JsonWebTokens` | 8.0.1 | MIT | runtime |
| `Microsoft.IdentityModel.Logging` | 8.0.1 | MIT | runtime |
| `Microsoft.IdentityModel.Protocols` | 8.0.1 | MIT | runtime |
| `Microsoft.IdentityModel.Protocols.OpenIdConnect` | 8.0.1 | MIT | runtime |
| `Microsoft.IdentityModel.Tokens` | 8.0.1 | MIT | runtime |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | test |
| `Microsoft.OpenApi` | 2.7.5 | MIT | runtime |
| `Microsoft.Testing.Platform` | 2.2.2 | MIT | runtime |
| `Microsoft.TestPlatform.ObjectModel` | 17.14.1 | MIT | test |
| `Microsoft.TestPlatform.TestHost` | 17.14.1 | MIT | test |
| `Microsoft.VisualStudio.SolutionPersistence` | 1.0.52 | MIT | runtime |
| `MimeKit` | 4.17.0 | MIT | runtime |
| `ModelContextProtocol.Core` | 2.0.0-preview.3 | Apache-2.0 | runtime |
| `Mono.TextTemplating` | 3.0.0 | MIT | runtime |
| `Newtonsoft.Json` | 13.0.4 | MIT | runtime |
| `Npgsql` | 10.0.2 | PostgreSQL | runtime |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.1 | PostgreSQL | runtime |
| `PdfPig` | 0.1.16 | Apache-2.0 | runtime |
| `QuestPDF` | 2025.1.5 | MIT (Community) or commercial QuestPDF Professional/Enterprise licence ⚠️ | runtime |
| `RBush.Signed` | 4.0.0 | MIT | runtime |
| `Scalar.AspNetCore` | 2.1.16 | MIT | runtime |
| `Serilog` | 4.2.0 | Apache-2.0 | runtime |
| `Serilog.AspNetCore` | 9.0.0 | Apache-2.0 | runtime |
| `Serilog.Enrichers.Environment` | 3.0.1 | Apache-2.0 | runtime |
| `Serilog.Enrichers.Thread` | 4.0.0 | Apache-2.0 | runtime |
| `Serilog.Extensions.Hosting` | 9.0.0 | Apache-2.0 | runtime |
| `Serilog.Extensions.Logging` | 9.0.0 | Apache-2.0 | runtime |
| `Serilog.Formatting.Compact` | 3.0.0 | Apache-2.0 | runtime |
| `Serilog.Settings.Configuration` | 9.0.0 | Apache-2.0 | runtime |
| `Serilog.Sinks.Console` | 6.0.0 | Apache-2.0 | runtime |
| `Serilog.Sinks.Debug` | 3.0.0 | Apache-2.0 | runtime |
| `Serilog.Sinks.File` | 6.0.0 | Apache-2.0 | runtime |
| `SixLabors.Fonts` | 1.0.0 | Apache-2.0 | runtime |
| `System.ClientModel` | 1.0.0 | MIT | runtime |
| `System.CodeDom` | 6.0.0 | MIT | runtime |
| `System.Composition` | 9.0.0 | MIT | runtime |
| `System.Composition.AttributedModel` | 9.0.0 | MIT | runtime |
| `System.Composition.Convention` | 9.0.0 | MIT | runtime |
| `System.Composition.Hosting` | 9.0.0 | MIT | runtime |
| `System.Composition.Runtime` | 9.0.0 | MIT | runtime |
| `System.Composition.TypedParts` | 9.0.0 | MIT | runtime |
| `System.IdentityModel.Tokens.Jwt` | 8.0.1 | MIT | runtime |
| `System.IO.Packaging` | 8.0.1 | MIT | runtime |
| `System.Memory.Data` | 1.0.2 | MIT | runtime |
| `System.Security.Cryptography.Pkcs` | 10.0.0 | MIT | runtime |
| `System.Security.Cryptography.ProtectedData` | 4.7.0 | MIT | runtime |
| `xunit` | 2.9.3 | Apache-2.0 | test |
| `xunit.abstractions` | 2.0.3 | Apache-2.0 | test |
| `xunit.analyzers` | 1.18.0 | Apache-2.0 | test |
| `xunit.assert` | 2.9.3 | Apache-2.0 | test |
| `xunit.core` | 2.9.3 | Apache-2.0 | test |
| `xunit.extensibility.core` | 2.9.3 | Apache-2.0 | test |
| `xunit.extensibility.execution` | 2.9.3 | Apache-2.0 | test |
| `xunit.runner.visualstudio` | 3.1.4 | Apache-2.0 | test |
| `YamlDotNet` | 16.2.0 | MIT | runtime |

### npm (frontend)

| Component | Version | Licence | Stage |
|---|---|---|---|
| `@antfu/install-pkg` | 1.1.0 | MIT | runtime |
| `@braintree/sanitize-url` | 7.1.2 | MIT | runtime |
| `@chevrotain/cst-dts-gen` | 12.0.0 | Apache-2.0 | runtime |
| `@chevrotain/gast` | 12.0.0 | Apache-2.0 | runtime |
| `@chevrotain/regexp-to-ast` | 12.0.0 | Apache-2.0 | runtime |
| `@chevrotain/types` | 12.0.0 | Apache-2.0 | runtime |
| `@chevrotain/utils` | 12.0.0 | Apache-2.0 | runtime |
| `@dagrejs/dagre` | 3.0.0 | MIT | runtime |
| `@dagrejs/graphlib` | 4.0.1 | MIT | runtime |
| `@esbuild/aix-ppc64` | 0.27.4 | MIT | runtime |
| `@esbuild/android-arm` | 0.27.4 | MIT | runtime |
| `@esbuild/android-arm64` | 0.27.4 | MIT | runtime |
| `@esbuild/android-x64` | 0.27.4 | MIT | runtime |
| `@esbuild/darwin-arm64` | 0.27.4 | MIT | runtime |
| `@esbuild/darwin-x64` | 0.27.4 | MIT | runtime |
| `@esbuild/freebsd-arm64` | 0.27.4 | MIT | runtime |
| `@esbuild/freebsd-x64` | 0.27.4 | MIT | runtime |
| `@esbuild/linux-arm` | 0.27.4 | MIT | runtime |
| `@esbuild/linux-arm64` | 0.27.4 | MIT | runtime |
| `@esbuild/linux-ia32` | 0.27.4 | MIT | runtime |
| `@esbuild/linux-loong64` | 0.27.4 | MIT | runtime |
| `@esbuild/linux-mips64el` | 0.27.4 | MIT | runtime |
| `@esbuild/linux-ppc64` | 0.27.4 | MIT | runtime |
| `@esbuild/linux-riscv64` | 0.27.4 | MIT | runtime |
| `@esbuild/linux-s390x` | 0.27.4 | MIT | runtime |
| `@esbuild/linux-x64` | 0.27.4 | MIT | runtime |
| `@esbuild/netbsd-arm64` | 0.27.4 | MIT | runtime |
| `@esbuild/netbsd-x64` | 0.27.4 | MIT | runtime |
| `@esbuild/openbsd-arm64` | 0.27.4 | MIT | runtime |
| `@esbuild/openbsd-x64` | 0.27.4 | MIT | runtime |
| `@esbuild/openharmony-arm64` | 0.27.4 | MIT | runtime |
| `@esbuild/sunos-x64` | 0.27.4 | MIT | runtime |
| `@esbuild/win32-arm64` | 0.27.4 | MIT | runtime |
| `@esbuild/win32-ia32` | 0.27.4 | MIT | runtime |
| `@esbuild/win32-x64` | 0.27.4 | MIT | runtime |
| `@floating-ui/core` | 1.7.5 | MIT | runtime |
| `@floating-ui/dom` | 1.7.6 | MIT | runtime |
| `@floating-ui/utils` | 0.2.11 | MIT | runtime |
| `@iconify/types` | 2.0.0 | MIT | runtime |
| `@iconify/utils` | 3.1.0 | MIT | runtime |
| `@internationalized/date` | 3.12.0 | Apache-2.0 | runtime |
| `@jridgewell/gen-mapping` | 0.3.13 | MIT | runtime |
| `@jridgewell/remapping` | 2.3.5 | MIT | runtime |
| `@jridgewell/resolve-uri` | 3.1.2 | MIT | runtime |
| `@jridgewell/sourcemap-codec` | 1.5.5 | MIT | runtime |
| `@jridgewell/trace-mapping` | 0.3.31 | MIT | runtime |
| `@mermaid-js/parser` | 1.1.0 | MIT | runtime |
| `@playwright/test` | 1.58.2 | Apache-2.0 | build |
| `@polka/url` | 1.0.0-next.29 | MIT | runtime |
| `@rollup/plugin-commonjs` | 29.0.2 | MIT | runtime |
| `@rollup/plugin-json` | 6.1.0 | MIT | runtime |
| `@rollup/plugin-node-resolve` | 16.0.3 | MIT | runtime |
| `@rollup/pluginutils` | 5.3.0 | MIT | runtime |
| `@rollup/rollup-android-arm-eabi` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-android-arm64` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-darwin-arm64` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-darwin-x64` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-freebsd-arm64` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-freebsd-x64` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-arm-gnueabihf` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-arm-musleabihf` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-arm64-gnu` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-arm64-musl` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-loong64-gnu` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-loong64-musl` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-ppc64-gnu` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-ppc64-musl` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-riscv64-gnu` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-riscv64-musl` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-s390x-gnu` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-x64-gnu` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-linux-x64-musl` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-openbsd-x64` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-openharmony-arm64` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-win32-arm64-msvc` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-win32-ia32-msvc` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-win32-x64-gnu` | 4.59.0 | MIT | runtime |
| `@rollup/rollup-win32-x64-msvc` | 4.59.0 | MIT | runtime |
| `@skeletonlabs/skeleton` | 4.15.1 | MIT | runtime |
| `@skeletonlabs/skeleton-common` | 4.15.1 | MIT | runtime |
| `@skeletonlabs/skeleton-svelte` | 4.15.1 | MIT | runtime |
| `@standard-schema/spec` | 1.1.0 | MIT | runtime |
| `@svelte-put/shortcut` | 4.1.0 | MIT | runtime |
| `@sveltejs/acorn-typescript` | 1.0.9 | MIT | runtime |
| `@sveltejs/adapter-auto` | 7.0.1 | MIT | build |
| `@sveltejs/adapter-node` | 5.5.4 | MIT | runtime |
| `@sveltejs/kit` | 2.55.0 | MIT | runtime |
| `@sveltejs/vite-plugin-svelte` | 6.2.4 | MIT | runtime |
| `@sveltejs/vite-plugin-svelte-inspector` | 5.0.2 | MIT | runtime |
| `@swc/helpers` | 0.5.21 | Apache-2.0 | runtime |
| `@tailwindcss/node` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-android-arm64` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-darwin-arm64` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-darwin-x64` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-freebsd-x64` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-linux-arm-gnueabihf` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-linux-arm64-gnu` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-linux-arm64-musl` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-linux-x64-gnu` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-linux-x64-musl` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-wasm32-wasi` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-win32-arm64-msvc` | 4.2.2 | MIT | runtime |
| `@tailwindcss/oxide-win32-x64-msvc` | 4.2.2 | MIT | runtime |
| `@tailwindcss/typography` | 0.5.19 | MIT | runtime |
| `@tailwindcss/vite` | 4.2.2 | MIT | runtime |
| `@types/cookie` | 0.6.0 | MIT | runtime |
| `@types/d3` | 7.4.3 | MIT | runtime |
| `@types/d3-array` | 3.2.2 | MIT | runtime |
| `@types/d3-axis` | 3.0.6 | MIT | runtime |
| `@types/d3-brush` | 3.0.6 | MIT | runtime |
| `@types/d3-chord` | 3.0.6 | MIT | runtime |
| `@types/d3-color` | 3.1.3 | MIT | runtime |
| `@types/d3-contour` | 3.0.6 | MIT | runtime |
| `@types/d3-delaunay` | 6.0.4 | MIT | runtime |
| `@types/d3-dispatch` | 3.0.7 | MIT | runtime |
| `@types/d3-drag` | 3.0.7 | MIT | runtime |
| `@types/d3-dsv` | 3.0.7 | MIT | runtime |
| `@types/d3-ease` | 3.0.2 | MIT | runtime |
| `@types/d3-fetch` | 3.0.7 | MIT | runtime |
| `@types/d3-force` | 3.0.10 | MIT | runtime |
| `@types/d3-format` | 3.0.4 | MIT | runtime |
| `@types/d3-geo` | 3.1.0 | MIT | runtime |
| `@types/d3-hierarchy` | 3.1.7 | MIT | runtime |
| `@types/d3-interpolate` | 3.0.4 | MIT | runtime |
| `@types/d3-path` | 3.1.1 | MIT | runtime |
| `@types/d3-polygon` | 3.0.2 | MIT | runtime |
| `@types/d3-quadtree` | 3.0.6 | MIT | runtime |
| `@types/d3-random` | 3.0.3 | MIT | runtime |
| `@types/d3-scale` | 4.0.9 | MIT | runtime |
| `@types/d3-scale-chromatic` | 3.1.0 | MIT | runtime |
| `@types/d3-selection` | 3.0.11 | MIT | runtime |
| `@types/d3-shape` | 3.1.8 | MIT | runtime |
| `@types/d3-time` | 3.0.4 | MIT | runtime |
| `@types/d3-time-format` | 4.0.3 | MIT | runtime |
| `@types/d3-timer` | 3.0.2 | MIT | runtime |
| `@types/d3-transition` | 3.0.9 | MIT | runtime |
| `@types/d3-zoom` | 3.0.8 | MIT | runtime |
| `@types/estree` | 1.0.8 | MIT | runtime |
| `@types/geojson` | 7946.0.16 | MIT | runtime |
| `@types/node` | 25.6.0 | MIT | runtime |
| `@types/resolve` | 1.20.2 | MIT | runtime |
| `@types/trusted-types` | 2.0.7 | MIT | runtime |
| `@typescript-eslint/types` | 8.57.1 | MIT | runtime |
| `@upsetjs/venn.js` | 2.0.0 | MIT | runtime |
| `@xyflow/svelte` | 1.5.1 | MIT | runtime |
| `@xyflow/system` | 0.0.75 | MIT | runtime |
| `@zag-js/accordion` | 1.39.0 | MIT | runtime |
| `@zag-js/anatomy` | 1.39.0 | MIT | runtime |
| `@zag-js/aria-hidden` | 1.39.0 | MIT | runtime |
| `@zag-js/auto-resize` | 1.39.0 | MIT | runtime |
| `@zag-js/avatar` | 1.39.0 | MIT | runtime |
| `@zag-js/carousel` | 1.39.0 | MIT | runtime |
| `@zag-js/collapsible` | 1.39.0 | MIT | runtime |
| `@zag-js/collection` | 1.39.0 | MIT | runtime |
| `@zag-js/combobox` | 1.39.0 | MIT | runtime |
| `@zag-js/core` | 1.39.0 | MIT | runtime |
| `@zag-js/date-picker` | 1.39.0 | MIT | runtime |
| `@zag-js/date-utils` | 1.39.0 | MIT | runtime |
| `@zag-js/dialog` | 1.39.0 | MIT | runtime |
| `@zag-js/dismissable` | 1.39.0 | MIT | runtime |
| `@zag-js/dom-query` | 1.39.0 | MIT | runtime |
| `@zag-js/file-upload` | 1.39.0 | MIT | runtime |
| `@zag-js/file-utils` | 1.39.0 | MIT | runtime |
| `@zag-js/floating-panel` | 1.39.0 | MIT | runtime |
| `@zag-js/focus-trap` | 1.39.0 | MIT | runtime |
| `@zag-js/focus-visible` | 1.39.0 | MIT | runtime |
| `@zag-js/i18n-utils` | 1.39.0 | MIT | runtime |
| `@zag-js/interact-outside` | 1.39.0 | MIT | runtime |
| `@zag-js/listbox` | 1.39.0 | MIT | runtime |
| `@zag-js/live-region` | 1.39.0 | MIT | runtime |
| `@zag-js/menu` | 1.39.0 | MIT | runtime |
| `@zag-js/pagination` | 1.39.0 | MIT | runtime |
| `@zag-js/popover` | 1.39.0 | MIT | runtime |
| `@zag-js/popper` | 1.39.0 | MIT | runtime |
| `@zag-js/progress` | 1.39.0 | MIT | runtime |
| `@zag-js/radio-group` | 1.39.0 | MIT | runtime |
| `@zag-js/rating-group` | 1.39.0 | MIT | runtime |
| `@zag-js/rect-utils` | 1.39.0 | MIT | runtime |
| `@zag-js/remove-scroll` | 1.39.0 | MIT | runtime |
| `@zag-js/scroll-snap` | 1.39.0 | MIT | runtime |
| `@zag-js/slider` | 1.39.0 | MIT | runtime |
| `@zag-js/steps` | 1.39.0 | MIT | runtime |
| `@zag-js/store` | 1.39.0 | MIT | runtime |
| `@zag-js/svelte` | 1.39.0 | MIT | runtime |
| `@zag-js/switch` | 1.39.0 | MIT | runtime |
| `@zag-js/tabs` | 1.39.0 | MIT | runtime |
| `@zag-js/tags-input` | 1.39.0 | MIT | runtime |
| `@zag-js/toast` | 1.39.0 | MIT | runtime |
| `@zag-js/toggle-group` | 1.39.0 | MIT | runtime |
| `@zag-js/tooltip` | 1.39.0 | MIT | runtime |
| `@zag-js/tree-view` | 1.39.0 | MIT | runtime |
| `@zag-js/types` | 1.39.0 | MIT | runtime |
| `@zag-js/utils` | 1.39.0 | MIT | runtime |
| `acorn` | 8.16.0 | MIT | runtime |
| `animejs` | 4.4.1 | MIT | runtime |
| `aria-query` | 5.3.1 | Apache-2.0 | runtime |
| `axobject-query` | 4.1.0 | Apache-2.0 | runtime |
| `chevrotain` | 12.0.0 | Apache-2.0 | runtime |
| `chevrotain-allstar` | 0.4.1 | MIT | runtime |
| `chokidar` | 4.0.3 | MIT | build |
| `clsx` | 2.1.1 | MIT | runtime |
| `commander` | 7.2.0 | MIT | runtime |
| `commander` | 8.3.0 | MIT | runtime |
| `commondir` | 1.0.1 | MIT | runtime |
| `confbox` | 0.1.8 | MIT | runtime |
| `cookie` | 0.6.0 | MIT | runtime |
| `cose-base` | 1.0.3 | MIT | runtime |
| `cose-base` | 2.2.0 | MIT | runtime |
| `croner` | 10.0.1 | MIT | runtime |
| `cssesc` | 3.0.0 | MIT | runtime |
| `csstype` | 3.2.3 | MIT | runtime |
| `cytoscape` | 3.33.2 | MIT | runtime |
| `cytoscape-cose-bilkent` | 4.1.0 | MIT | runtime |
| `cytoscape-fcose` | 2.2.0 | MIT | runtime |
| `d3` | 7.9.0 | ISC | runtime |
| `d3-array` | 3.2.4 | ISC | runtime |
| `d3-array` | 2.12.1 | BSD-3-Clause | runtime |
| `d3-axis` | 3.0.0 | ISC | runtime |
| `d3-brush` | 3.0.0 | ISC | runtime |
| `d3-chord` | 3.0.1 | ISC | runtime |
| `d3-color` | 3.1.0 | ISC | runtime |
| `d3-contour` | 4.0.2 | ISC | runtime |
| `d3-delaunay` | 6.0.4 | ISC | runtime |
| `d3-dispatch` | 3.0.1 | ISC | runtime |
| `d3-drag` | 3.0.0 | ISC | runtime |
| `d3-dsv` | 3.0.1 | ISC | runtime |
| `d3-ease` | 3.0.1 | BSD-3-Clause | runtime |
| `d3-fetch` | 3.0.1 | ISC | runtime |
| `d3-force` | 3.0.0 | ISC | runtime |
| `d3-format` | 3.1.2 | ISC | runtime |
| `d3-geo` | 3.1.1 | ISC | runtime |
| `d3-hierarchy` | 3.1.2 | ISC | runtime |
| `d3-interpolate` | 3.0.1 | ISC | runtime |
| `d3-path` | 3.1.0 | ISC | runtime |
| `d3-path` | 1.0.9 | BSD-3-Clause | runtime |
| `d3-polygon` | 3.0.1 | ISC | runtime |
| `d3-quadtree` | 3.0.1 | ISC | runtime |
| `d3-random` | 3.0.1 | ISC | runtime |
| `d3-sankey` | 0.12.3 | BSD-3-Clause | runtime |
| `d3-scale` | 4.0.2 | ISC | runtime |
| `d3-scale-chromatic` | 3.1.0 | ISC | runtime |
| `d3-selection` | 3.0.0 | ISC | runtime |
| `d3-shape` | 1.3.7 | BSD-3-Clause | runtime |
| `d3-shape` | 3.2.0 | ISC | runtime |
| `d3-time` | 3.1.0 | ISC | runtime |
| `d3-time-format` | 4.1.0 | ISC | runtime |
| `d3-timer` | 3.0.1 | ISC | runtime |
| `d3-transition` | 3.0.1 | ISC | runtime |
| `d3-zoom` | 3.0.0 | ISC | runtime |
| `dagre-d3-es` | 7.0.14 | MIT | runtime |
| `dayjs` | 1.11.20 | MIT | runtime |
| `deepmerge` | 4.3.1 | MIT | runtime |
| `delaunator` | 5.1.0 | ISC | runtime |
| `detect-libc` | 2.1.2 | Apache-2.0 | runtime |
| `devalue` | 5.6.4 | MIT | runtime |
| `dompurify` | 3.4.1 | (MPL-2.0 OR Apache-2.0) | runtime |
| `enhanced-resolve` | 5.20.1 | MIT | runtime |
| `esbuild` | 0.27.4 | MIT | runtime |
| `esm-env` | 1.2.2 | MIT | runtime |
| `esrap` | 2.2.4 | MIT | runtime |
| `estree-walker` | 2.0.2 | MIT | runtime |
| `fdir` | 6.5.0 | MIT | runtime |
| `fsevents` | 2.3.3 | MIT | runtime |
| `fsevents` | 2.3.2 | MIT | build |
| `function-bind` | 1.1.2 | MIT | runtime |
| `graceful-fs` | 4.2.11 | ISC | runtime |
| `hachure-fill` | 0.5.2 | MIT | runtime |
| `hasown` | 2.0.2 | MIT | runtime |
| `highlight.js` | 11.11.1 | BSD-3-Clause | runtime |
| `iconv-lite` | 0.6.3 | MIT | runtime |
| `internmap` | 1.0.1 | ISC | runtime |
| `internmap` | 2.0.3 | ISC | runtime |
| `is-core-module` | 2.16.1 | MIT | runtime |
| `is-module` | 1.0.0 | MIT | runtime |
| `is-reference` | 1.2.1 | MIT | runtime |
| `is-reference` | 3.0.3 | MIT | runtime |
| `jiti` | 2.6.1 | MIT | runtime |
| `katex` | 0.16.45 | MIT | runtime |
| `khroma` | 2.1.0 | NOASSERTION ⚠️ | runtime |
| `kleur` | 4.1.5 | MIT | runtime |
| `langium` | 4.2.2 | MIT | runtime |
| `layout-base` | 2.0.1 | MIT | runtime |
| `layout-base` | 1.0.2 | MIT | runtime |
| `lightningcss` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-android-arm64` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-darwin-arm64` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-darwin-x64` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-freebsd-x64` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-linux-arm-gnueabihf` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-linux-arm64-gnu` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-linux-arm64-musl` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-linux-x64-gnu` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-linux-x64-musl` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-win32-arm64-msvc` | 1.32.0 | MPL-2.0 | runtime |
| `lightningcss-win32-x64-msvc` | 1.32.0 | MPL-2.0 | runtime |
| `locate-character` | 3.0.0 | MIT | runtime |
| `lodash-es` | 4.18.1 | MIT | runtime |
| `lucide-svelte` | 1.0.1 | ISC | runtime |
| `magic-string` | 0.30.21 | MIT | runtime |
| `marked` | 17.0.5 | MIT | runtime |
| `marked` | 16.4.2 | MIT | runtime |
| `mermaid` | 11.14.0 | MIT | runtime |
| `mlly` | 1.8.2 | MIT | runtime |
| `mri` | 1.2.0 | MIT | build |
| `mrmime` | 2.0.1 | MIT | runtime |
| `nanoid` | 3.3.11 | MIT | runtime |
| `obug` | 2.1.1 | MIT | runtime |
| `package-manager-detector` | 1.6.0 | MIT | runtime |
| `path-data-parser` | 0.1.0 | MIT | runtime |
| `path-parse` | 1.0.7 | MIT | runtime |
| `pathe` | 2.0.3 | MIT | runtime |
| `picocolors` | 1.1.1 | ISC | runtime |
| `picomatch` | 4.0.3 | MIT | runtime |
| `pkg-types` | 1.3.1 | MIT | runtime |
| `playwright` | 1.58.2 | Apache-2.0 | build |
| `playwright-core` | 1.58.2 | Apache-2.0 | build |
| `points-on-curve` | 0.2.0 | MIT | runtime |
| `points-on-path` | 0.2.1 | MIT | runtime |
| `postcss` | 8.5.8 | MIT | runtime |
| `postcss-selector-parser` | 6.0.10 | MIT | runtime |
| `proxy-compare` | 3.0.1 | MIT | runtime |
| `readdirp` | 4.1.2 | MIT | build |
| `resolve` | 1.22.11 | MIT | runtime |
| `robust-predicates` | 3.0.3 | Unlicense | runtime |
| `rollup` | 4.59.0 | MIT | runtime |
| `roughjs` | 4.6.6 | MIT | runtime |
| `rw` | 1.3.3 | BSD-3-Clause | runtime |
| `sade` | 1.8.1 | MIT | build |
| `safer-buffer` | 2.1.2 | MIT | runtime |
| `set-cookie-parser` | 3.0.1 | MIT | runtime |
| `sirv` | 3.0.2 | MIT | runtime |
| `source-map-js` | 1.2.1 | BSD-3-Clause | runtime |
| `stylis` | 4.4.0 | MIT | runtime |
| `supports-preserve-symlinks-flag` | 1.0.0 | MIT | runtime |
| `svelte` | 5.54.0 | MIT | runtime |
| `svelte-check` | 4.4.5 | MIT | build |
| `tailwindcss` | 4.2.2 | MIT | runtime |
| `tapable` | 2.3.0 | MIT | runtime |
| `tinyexec` | 1.1.1 | MIT | runtime |
| `tinyglobby` | 0.2.15 | MIT | runtime |
| `totalist` | 3.0.1 | MIT | runtime |
| `ts-dedent` | 2.2.0 | MIT | runtime |
| `tslib` | 2.8.1 | 0BSD | runtime |
| `typescript` | 5.9.3 | Apache-2.0 | runtime |
| `ufo` | 1.6.3 | MIT | runtime |
| `undici-types` | 7.19.2 | MIT | runtime |
| `util-deprecate` | 1.0.2 | MIT | runtime |
| `uuid` | 11.1.0 | MIT | runtime |
| `vite` | 7.3.1 | MIT | runtime |
| `vitefu` | 1.1.2 | MIT | runtime |
| `vscode-jsonrpc` | 8.2.0 | MIT | runtime |
| `vscode-languageserver` | 9.0.1 | MIT | runtime |
| `vscode-languageserver-protocol` | 3.17.5 | MIT | runtime |
| `vscode-languageserver-textdocument` | 1.0.12 | MIT | runtime |
| `vscode-languageserver-types` | 3.17.5 | MIT | runtime |
| `vscode-uri` | 3.1.0 | MIT | runtime |
| `zimmerframe` | 1.1.4 | MIT | runtime |
| `zod` | 3.25.76 | MIT | runtime |

### PyPI (Python runtime inside the backend image)

| Component | Version | Licence | Stage |
|---|---|---|---|
| `bcrypt` | 5.0.0 | Apache-2.0 | runtime |
| `cffi` | 2.1.1 | MIT-0 | runtime |
| `cryptography` | 50.0.1 | Apache-2.0 OR BSD-3-Clause | runtime |
| `invoke` | 3.0.3 | BSD-2-Clause | runtime |
| `markdown-it-py` | 4.2.0 | MIT | runtime |
| `mdurl` | 0.1.2 | MIT | runtime |
| `netmiko` | 4.7.0 | MIT | runtime |
| `ntc_templates` | 9.2.0 | Apache-2.0 | runtime |
| `paramiko` | 4.0.0 | LGPL-2.1 ⚠️ | runtime |
| `pycparser` | 3.0 | BSD-3-Clause | runtime |
| `Pygments` | 2.21.0 | BSD-2-Clause | runtime |
| `PyNaCl` | 1.6.2 | Apache-2.0 | runtime |
| `pyserial` | 3.5 | BSD-3-Clause | runtime |
| `PyYAML` | 6.0.3 | MIT | runtime |
| `rich` | 15.0.0 | MIT | runtime |
| `ruamel.yaml` | 0.19.1 | MIT | runtime |
| `scp` | 0.16.1 | LGPL-2.1-or-later | runtime |
| `textfsm` | 2.1.0 | Apache-2.0 | runtime |

### Operating-system packages (Debian, backend image)

Installed with `apt-get` on top of the base image. **Deliberately unpinned:** pinning apt versions on Debian breaks the build as soon as the mirror drops the previous version after a point release. The exact version of each package exists only inside a built image and must be extracted from it (`dpkg-query`) for the notice of a specific release.

| Component | Why it is present | Licence |
|---|---|---|
| `ansible` | Ansible handler invokes ansible-playbook | NOASSERTION |
| `bubblewrap` | Python execution sandbox (Python:SandboxMode=bwrap) | NOASSERTION |
| `iputils-ping` | reachability checks (System.Net ping shells out) | NOASSERTION |
| `python3` | interpreter for Python snippets | NOASSERTION |
| `python3-pip` | installs netmiko/paramiko/textfsm/ntc-templates | NOASSERTION |
| `sshpass` | password authentication for Ansible SSH | NOASSERTION |
| `util-linux` | prlimit: memory, CPU, fsize and nproc caps | NOASSERTION |

### Base images

Pinned by `sha256` digest as of 10 September 2026. Base images are not redistributed as source, but they are part of the deployed artifact: their own notices travel inside the image and must be collected from it for a release.

| Image | Tag | Why it is present | Stage |
|---|---|---|---|
| `cloudflare/cloudflared` | `latest` | publishing tunnel | runtime |
| `mcr.microsoft.com/dotnet/aspnet` | `10.0` | backend runtime image | runtime |
| `mcr.microsoft.com/dotnet/sdk` | `10.0` | backend compilation | build |
| `node` | `22-alpine` | frontend deps, build and runtime | build |
| `postgres` | `17-alpine` | database for the compose deployment | runtime |

## 4. What is still missing before this is a release notice

An inventory of components and licences is not yet a complete notice. A published release must add:

1. **The licence texts that must accompany the distribution.** Apache 2.0 requires shipping the licence text and preserving the `NOTICE` of every Apache dependency that carries one; MIT and BSD require reproducing their copyright lines. Those texts are extracted from the artifacts themselves at release time.
2. **The libgit2 linking exception**, reproduced verbatim next to the `LibGit2Sharp.NativeBinaries` notice.
3. **Resolution of the undeclared licences:** `khroma`, and the exact versions and licences of the operating-system packages, read from the built image.
4. **The commercial decision on QuestPDF and json-everything**, recorded in writing, including which tier applies to Isaza Labs LLC and any licence purchased.
5. **Non-software assets:** fonts, icons, logos and screenshots included in the distribution. The SBOM covers code dependencies; graphical assets are reviewed separately against `assets/ASSET_MANIFEST.csv`.
6. **A statement of modified material.** No component in this list is recorded as modified. If a release carries a local patch over a dependency, it must be declared here.

## 5. Regenerating this file

Since `deploy/requirements.txt`, the `packages.lock.json` files and the image digests are pinned, regeneration is reproducible: the same commit yields the same inventory. The procedure is the one described at the top of this file, and its output is the CycloneDX SBOM from which this document is derived.
