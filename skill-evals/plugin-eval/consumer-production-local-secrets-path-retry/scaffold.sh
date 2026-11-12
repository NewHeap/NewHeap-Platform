#!/usr/bin/env bash
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
# Prepares the database-read workspace: The management-portal workspace with a pinned newheap-db tool at the repository root and one checked-in database-read profile, local instructions and typed requests under src/Back-end.
set -euo pipefail

case_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fixture="$case_directory/../fixtures/database-read"
if [ ! -d "$fixture" ]; then
  echo "The database-read workspace fixture is missing. Run the suite with npm run skills:eval:run, which materializes it." >&2
  exit 1
fi
cp -R "$fixture/." .

# A repository with the fixture committed lets the agent and a reviewer see what the run changed.
if command -v git >/dev/null 2>&1; then
  {
    git init --quiet
    git add --all
    git -c user.name="NewHeap eval" -c user.email="eval@example.invalid" -c commit.gpgsign=false commit --quiet --message "database-read workspace"
  } || echo "Git initialization of the workspace was skipped." >&2
fi
