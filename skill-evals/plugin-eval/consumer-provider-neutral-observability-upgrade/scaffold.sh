#!/usr/bin/env bash
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
# Prepares the management-portal workspace: A consumer repository right after the foundation bootstrap of a PostgreSQL management portal, before package restore and feature work.
set -euo pipefail

case_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fixture="$case_directory/../fixtures/management-portal"
if [ ! -d "$fixture" ]; then
  echo "The management-portal workspace fixture is missing. Run the suite with npm run skills:eval:run, which materializes it." >&2
  exit 1
fi
cp -R "$fixture/." .

# A repository with the fixture committed lets the agent and a reviewer see what the run changed.
if command -v git >/dev/null 2>&1; then
  {
    git init --quiet
    git add --all
    git -c user.name="NewHeap eval" -c user.email="eval@example.invalid" -c commit.gpgsign=false commit --quiet --message "management-portal workspace"
  } || echo "Git initialization of the workspace was skipped." >&2
fi
