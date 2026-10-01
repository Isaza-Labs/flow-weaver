# Third-party dependency and content policy

## Intake requirements

No dependency, vendored component, copied snippet, font, image, dataset, model or generated bundle may enter a release without:

- exact name and version or immutable source reference;
- upstream URL and supplier;
- detected and verified license expression;
- copyright and attribution notices;
- distribution form: source, linked library, executable, container layer, build-only tool or hosted service;
- modification status;
- known source-availability, notice, patent or commercial conditions;
- reviewer and decision.

## Decision classes

- **ALLOW:** compatible obligations are understood and automated.
- **ALLOW WITH CONDITIONS:** release requires a notice, source offer, replacement mechanism, paid entitlement or other recorded action.
- **ESCALATE:** license is missing, conflicting, custom, copyleft-sensitive, data-restrictive or commercially conditional.
- **DENY:** rights are insufficient or obligations cannot be met.

## FlowWeaver-specific gates

The dependency inventory in [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) identifies items requiring release attention, including QuestPDF's commercial threshold, LGPL/GPL components in runtime images, packages with linking exceptions, optional maintenance terms and alternative license expressions. These must be revalidated against the resolved dependencies of each release; an earlier inventory is not a permanent approval.

## Vendoring

Prefer package-manager resolution over copying source. If vendoring is necessary, preserve the upstream license and notices in or next to the component, record modifications and add the component to `THIRD-PARTY-NOTICES.md`.

## Containers

An application license does not replace the licenses of operating-system packages. Produce an image SBOM, retain applicable package copyright files and satisfy any source-code or offer obligations for distributed copyleft components.

## No automatic relicensing

Third-party content never becomes Apache-2.0 merely because it is stored in the FlowWeaver repository. Its original license remains controlling.

