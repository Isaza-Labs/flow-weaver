#!/usr/bin/env bash
# One-shot migration of the admin-approved pip packages that python_snippet
# uses, from the old per-company layout to the single-tenant one.
#
#   before:  /app/pyenv/<companyId-without-dashes>/...
#   after:   /app/pyenv/site/...
#
# Run this ONCE, inside the worker container, after deploying the build that
# removed multi-tenancy and BEFORE the first python_snippet run that needs an
# extra package. Until it runs, the sandbox simply finds no package directory
# and the snippet fails on `import <pkg>` — no data is lost either way.
#
#   docker compose exec worker bash /app/deploy/ops/migrate-pyenv-to-site.sh
#
# Idempotent: re-running once /site exists is a no-op.
#
# FALLBACK: if the move fails or the old directories are unusable, just delete
# them. PythonPackageProvisionerHostedService reinstalls everything into /site
# from the allowed_python_modules rows on its next sweep.

set -euo pipefail

PYENV_ROOT="${PYTHON_PACKAGES_DIR:-/app/pyenv}"
SITE_DIR="$PYENV_ROOT/site"

if [ ! -d "$PYENV_ROOT" ]; then
    echo "nothing to do: $PYENV_ROOT does not exist"
    exit 0
fi

# Old per-company dirs are 32-char hex (Guid "N" format). Anything else —
# including `site` itself — is left alone.
mapfile -t company_dirs < <(
    find "$PYENV_ROOT" -mindepth 1 -maxdepth 1 -type d \
        -regextype posix-extended -regex '.*/[0-9a-fA-F]{32}$' | sort
)

if [ "${#company_dirs[@]}" -eq 0 ]; then
    echo "nothing to do: no per-company package directories under $PYENV_ROOT"
    exit 0
fi

if [ "${#company_dirs[@]}" -gt 1 ]; then
    echo "found ${#company_dirs[@]} per-company package directories:"
    printf '  %s\n' "${company_dirs[@]}"
    echo
    echo "This deployment held more than one company. Only ONE can survive as the"
    echo "single-tenant package set. Re-run with the canonical one pinned:"
    echo "  CANONICAL_DIR=<path> $0"
    : "${CANONICAL_DIR:?refusing to guess which company's packages to keep}"
    source_dir="$CANONICAL_DIR"
else
    source_dir="${company_dirs[0]}"
fi

echo "migrating $source_dir -> $SITE_DIR"
mkdir -p "$SITE_DIR"
# -n: never clobber a package already installed under site/.
cp -rn "$source_dir/." "$SITE_DIR/"
echo "copied. Old directory left in place; remove it once a python_snippet run"
echo "has confirmed the packages resolve:"
printf '  rm -rf %s\n' "${company_dirs[@]}"
