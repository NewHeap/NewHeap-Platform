#!/usr/bin/env bash
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
# Prepares the empty workspace: An empty Git repository.
set -euo pipefail

if command -v git >/dev/null 2>&1; then
  git init --quiet || echo "Git initialization of the workspace was skipped." >&2
fi
