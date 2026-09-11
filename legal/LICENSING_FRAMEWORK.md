# FlowWeaver licensing framework

**Owner and steward:** Isaza Labs LLC  
**First-party license:** Apache License 2.0 (`Apache-2.0`)  
**Effective project decision:** 14 August 2026

## Coverage matrix

| Repository element | Rule | Required evidence |
|---|---|---|
| Backend source, migrations and configuration schemas | Apache-2.0 | Root `LICENSE`; SPDX metadata |
| Automated tests and fixtures created for FlowWeaver | Apache-2.0 | Synthetic or authorized fixture source |
| Frontend source, styles and first-party UI components | Apache-2.0 | SPDX metadata; asset provenance |
| Deployment scripts, Dockerfiles, Compose and CI configuration | Apache-2.0 | Base-image and package notices |
| Public documentation and diagrams created for FlowWeaver | Apache-2.0 | Source attribution when adapted |
| Skills, specs and workflows created for FlowWeaver | Apache-2.0 | DCO for external contributions |
| Schemas, contracts, examples and conformance vectors | Apache-2.0 | Version and provenance |
| Internal developer tools and release scripts | Apache-2.0 | Dependency inventory |
| First-party images, icons, fonts and logos | Copyright licensed under Apache-2.0; trademark rights remain separate | Asset manifest and `TRADEMARK.md` |
| Synthetic sample data | Apache-2.0 | Statement that it is synthetic and contains no personal data |
| External contributions accepted into the project | Apache-2.0 | DCO sign-off and review |
| Third-party libraries, vendored code, fonts and media | Original upstream license | Full license/notice and provenance |
| Operating-system packages and container layers | Original upstream licenses | Image SBOM and applicable source/notice offer |
| Generated SBOMs and first-party release manifests | Apache-2.0 as project documents; embedded third-party facts remain attributed | Release hash and generation method |
| User/customer workflows, prompts, credentials and data | Not automatically contributed or relicensed | Customer authorization and product terms |
| Model weights or datasets from external providers | Excluded unless individually approved | Separate license and distribution decision |
| Secrets, private keys and certificates | Never distributable | Secret scan and exclusion evidence |
| Private corporate agreements and chain-of-title evidence | Outside the public repository | Private dossier access controls |

## One repository license

`/LICENSE` is the authoritative first-party license. Verbatim Apache 2.0 copies in independently distributable subdirectories are permitted but must not introduce different terms. `LICENSES/Apache-2.0.txt` exists for SPDX/REUSE tooling.

## What Apache 2.0 does not change

- Third-party material keeps its original license.
- Trademark rights are not broadly granted.
- Customer or user content is not donated merely because it passes through the software.
- Apache 2.0 grants broad copyright and patent permissions; it does not by itself transfer ownership to Isaza Labs LLC.
- Private legal instruments do not belong in a public source distribution.

## Exceptions

Any proposed exception requires a written component decision recording origin, license, compatibility, distribution form, obligations, approver and release scope. “Found online” or “free to download” is not a license.

