# Releasing

How a FlowWeaver version is published. A version exists once its tag and GitHub release exist. Until then it is only a changelog entry.

## 1. Prepare the release pull request

Set the same version, `X.Y.Z`, in every place that declares one:

| File | What to change |
|---|---|
| `flow_weaver_backend/flow_weaver_backend.csproj` | `<Version>X.Y.Z</Version>` (bundle exports report it as `exported_by.version`) |
| `frontend/package.json` and `frontend/package-lock.json` | `"version": "X.Y.Z"`, changed with `npm version X.Y.Z --no-git-tag-version` from `frontend/` |
| `CHANGELOG.md` | Rename the entry to `## [X.Y.Z] - YYYY-MM-DD`, using the date the tag will be created. Add `[X.Y.Z]: https://github.com/Isaza-Labs/flow-weaver/releases/tag/vX.Y.Z` and point `[Unreleased]` at `compare/vX.Y.Z...HEAD`. |
| `README.md` | The version badge: `version-X.Y.Z_pre--release-orange` for a pre-release, linked to the release page |
| `SECURITY.md` | The supported-versions table |

Put any action an existing deployment must take before upgrading under "Upgrade notes" at the top of the changelog entry.

## 2. Merge and wait for the checks

Merge the pull request through the normal route. The required `backend`, `frontend` and `repository` checks must pass on the merge commit on `main`. Do not tag a commit whose checks are pending or failing.

## 3. Tag and publish

Create the tag and the release together, on the merge commit:

```bash
gh release create vX.Y.Z --target <merge commit sha> --prerelease \
  --title "FlowWeaver X.Y.Z (pre-release)" --notes-file <release notes>
```

Drop `--prerelease` from 1.0 on. The release notes cover purpose and scope, pre-1.0 status, installation (with `git clone --branch vX.Y.Z --depth 1`), the first example, relevant changes, known limitations, support, and upgrade notes. Link to files at the tag (`blob/vX.Y.Z/...`), not at `main`.

A published tag is never moved. A fix found after publishing ships in the next version.

## 4. After publishing

- In `QUICK_START_GUIDE.md`, show the clone command for the published tag (`git clone --branch vX.Y.Z --depth 1 ...`) next to the `main` command.
- Check that the release page, the tag, the badge link and the changelog links all resolve.
