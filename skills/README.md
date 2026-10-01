# Skills

Reusable agent instructions and operational playbooks for FlowWeaver that
are **not** shipped with the product: skills you can add to a deployment
when you need them. This directory currently contains only this README.

The agent's built-in skills are not here. They live in
[`flow_weaver_backend/Skills/`](../flow_weaver_backend/Skills/) (one `*.md`
file per skill, plus the `vendors/` command catalogues) and are synced into
the deployment's prompt-skill catalogue every time the backend starts.

## Using a skill from this directory

A skill is a Markdown file. An admin adds it to a deployment from
**AI → Skills** (`/ai/skills`, create or upload), optionally scoped to an
Integration so it is only loaded when that integration is in play.

## Contributing a skill

- Keep each skill self-contained and versioned.
- Document required tools, permissions, inputs, outputs and failure modes.
- Do not include credentials, customer data, real infrastructure
  identifiers or undocumented remote dependencies.
- Validate skills in a disposable environment before publication.

First-party content in this directory is licensed under the root Apache 2.0 license.
