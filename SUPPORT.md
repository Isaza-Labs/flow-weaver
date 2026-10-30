# Support

Use the channel that matches the request:

- **Setup and usage questions:** start with the [quick start](./QUICK_START_GUIDE.md) and the [documentation index](./docs/README.md), then ask in [Questions and help](https://github.com/Isaza-Labs/flow-weaver/discussions/categories/q-a) (GitHub Discussions, Q&A category). Include the details listed under "What to include" below.
- **Reproducible product defect:** open a [bug report](https://github.com/Isaza-Labs/flow-weaver/issues/new/choose). Include version or commit, deployment mode, and the minimal steps that reproduce it.
- **Feature, integration or documentation proposal:** open a feature request or an integration proposal from the same [issue chooser](https://github.com/Isaza-Labs/flow-weaver/issues/new/choose).
- **Security vulnerability, exposed secret or active incident:** follow [SECURITY.md](./SECURITY.md). Never use a public issue, pull request or discussion for this.
- **Licensing, trademark or commercial question:** write to the address in the table below. Do not use a public issue for confidential terms.

## Contact routes

Each address is a monitored shared mailbox, not a personal one, so a request does not wait on one person being available. Name the product in the subject line.

| Purpose | Address |
|---|---|
| Security vulnerability, exposed secret, active incident | `security@networkingdev.com` |
| Code of conduct concern | `conduct@networkingdev.com` |
| Trademark permission request | `trademark@networkingdev.com` |
| Licensing, contracts, legal | `legal@networkingdev.com` |
| Commercial and sales enquiries | `sales@networkingdev.com` |
| Anything else | `contact@networkingdev.com` |

FlowWeaver is owned and licensed by **Isaza Labs LLC** and is published under the NetworkingDev brand, which is why these addresses use the `networkingdev.com` domain. Ownership, licensing and trademark rights sit with Isaza Labs LLC regardless of the brand a page or a mailbox carries. See [GOVERNANCE.md](./GOVERNANCE.md).

## What support is, and is not

Answers here are best effort by the maintainers. No public support service-level agreement is in force unless it is established in a separate written agreement. Response targets for security reports are stated in [SECURITY.md](./SECURITY.md) and are operating targets, not contractual commitments.

## What to include

A question or a bug report about a deployment you run yourself is answered faster with:

- **Version or commit:** the release tag you installed, or `git rev-parse --short HEAD` in your clone.
- **Installation mode:** setup wizard with Docker, `run.sh`/`run.ps1` without the wizard, or native.
- **Symptom:** what you did, what you expected and what happened.
- **Relevant log lines:** only the part around the error, for example from `bash ./run.sh logs backend`.

Remove credentials, tokens, customer data, internal hostnames and addresses first. Do not paste a `.env` file, a keyring path or a live token into any public channel.
