# Source header templates

Use short SPDX headers in new first-party files. Do not add a FlowWeaver copyright notice to the unmodified Apache license text or to third-party files that Isaza Labs LLC does not own.

## C#, JavaScript and TypeScript

```text
// SPDX-FileCopyrightText: 2026 Isaza Labs LLC
// SPDX-License-Identifier: Apache-2.0
```

## Python, shell, YAML and TOML

```text
# SPDX-FileCopyrightText: 2026 Isaza Labs LLC
# SPDX-License-Identifier: Apache-2.0
```

## HTML and Markdown

```text
<!--
SPDX-FileCopyrightText: 2026 Isaza Labs LLC
SPDX-License-Identifier: Apache-2.0
-->
```

## JSON and generated files

JSON has no comment syntax. Use `REUSE.toml`, an adjacent `.license` file or generator metadata. Do not make the JSON invalid by inserting comments.

## Modified third-party files

Preserve upstream notices and add a clear modification statement, for example:

```text
Modifications Copyright 2026 Isaza Labs LLC.
Modified by Isaza Labs LLC on YYYY-MM-DD: <summary>.
```

Use the upstream license expression, not automatically `Apache-2.0`.

