# Skill evals

`skill-evals/evals.json` is the canonical list of consumer skill evals: a realistic prompt, the target skill, the guidance rules it exercises and the expected outcome. `npm run skills:eval` checks that every rule is covered and that the generated suite is current; it calls no model. `npm run skills:eval:run` measures whether the distributed `newheap-platform` plugin actually improves an agent's result, by running the cases with `claude plugin eval` with and without the plugin.

## Edit a case

Change only `skill-evals/evals.json` and the fixture overlays under `skill-evals/fixtures`, then run `npm run guidance:generate`. It writes the `claude plugin eval` suite to `skill-evals/plugin-eval/<case-id>/`; never edit those files by hand. `npm run skills:eval` and `npm run guidance:check` fail while the suite is stale.

- `workspace` names the starting repository from the top-level `workspaces`; `defaultWorkspace` applies when a case names none. `empty` is an empty Git repository, `management-portal` is the bootstrap output of the distributed foundation script for a PostgreSQL management portal (made with `--skip-install` when the suite is staged, so it always matches the current bootstrapper), and `database-read` adds the pinned `newheap-db` tool and a checked-in diagnostics contract from `skill-evals/fixtures/database-read`. A fixture overlay may not contain `prompt.md` or `case.yaml`.
- `checks` adds deterministic graders where the expected outcome is cheaply checkable: `file_exists` and `file_matches` for files the agent writes in the workspace, `response_matches` and `response_excludes` for its final reply, and `module_not_read` for an internal module that the router must not load. Prefer them to rubric wording for exact flags, option names and file locations; a pattern over a long reply is more stable than a judge.

Each generated case has an `outcome` rubric grader (a judge model scores the final reply against `expectedOutcome`, weighted as much as the case's scored checks together), the checks, and plugin-fired indicators: `skill-fired` for the `newheap-platform-development` router and `module-<directory>` for each internal module the case must read. Indicators can only pass with the plugin, so they never count toward the score or the delta. `maintainer-public-change` targets this maintainer skill, which the plugin does not distribute, so it has no generated case.

Every run gets an empty temporary repository prepared by the case's `scaffold.sh`, the Read, Glob, Grep, Skill, Write and Edit tools, at most 30 turns and 15 minutes. It cannot run shell commands unless `--allow-bash` is passed, so the appended system prompt asks the agent to state the commands it would run and to end with a summary of the guidance, decisions, files and commands; the rubric judges that summary.

## When to run

Every run is a paid model call on your own Claude account, and the runner refuses to start without an explicit selection. It is not part of CI.

- While changing a skill, run the cases of the changed module once: `npm run skills:eval:run -- --tag frontend --runs 1`.
- Before releasing a changed consumer skill, rule or router description, run the affected cases with the baseline and the default three runs, and pin the models so results stay comparable: `npm run skills:eval:run -- --tag frontend --baseline --model sonnet --judge-model sonnet --max-cost-usd 10`.
- After a large guidance change or a new model generation, run `--all --baseline` with a cost ceiling.

A single case is `--case <id>`; `--case` takes one glob such as `consumer-auth-*`, and `--tag` takes a module directory (`foundation`, `backend`, `frontend`, `authentication`, `database`, `media`, `background-processing`, `runtime-configuration`, `testing`) or `router`, and may repeat. `--dry-run` stages everything and prints the `claude plugin eval` command without running it. On Windows, Bash cannot be granted, because Claude Code has no sandbox backend there; run `--allow-bash` suites under WSL2, macOS or Linux.

The runner needs Claude Code 2.1.269 or later and Git 2.31 or later. It copies `plugins/newheap-platform` to `skill-evals/.staging/newheap-platform`, adds the `.claude-plugin/plugin.json` that `claude plugin eval` requires (the distributed plugin ships only the Codex manifest), adds the suite as `evals/` and materializes the workspaces the selected cases use. Results go to `skill-evals/results/<timestamp>/`; both directories are ignored by Git. The first run in a checkout asks to trust the staged plugin directory; `--trust-plugin` answers that prompt for non-interactive use. Reports stay local unless `--publish` is passed.

## Read the result

`claude plugin eval` prints a line per run with each grader's verdict, then its own table. The runner adds a table per case:

- `WITH` is the mean with-plugin score, `W/OUT` the no-plugin score and `DELTA` their difference, only with `--baseline`. A positive delta is what the skill contributed. A delta near zero means the case passes or fails equally without the plugin: either the base model already knows the behavior or the skill did not change the outcome.
- `SKILL` and `MODULES` count the with-plugin runs in which the router skill fired and every expected module was read. A skill that never fires points at the router `description`; a fired skill with the wrong modules points at the router's module list; both firing with a low score points at the module's content or at the rubric.
- `NOTE` counts runs that ended with an error, such as a timeout or the turn limit.

Open `report.html` in the results directory before changing a skill: every failed grader shows its explanation, and the rubric grader shows the judge's votes and the reply it judged. When an indicator passes but the score is low, suspect the judge before the skill: rerun with `--judge-model sonnet`, or replace a fragile rubric element with a check. Leave a partial run (cost ceiling, interruption or authentication failure) out of comparisons, and compare deltas only between runs with the same `--model`, `--judge-model` and `--runs`. Keep the results out of the repository; record a material finding in the change or release notes instead.
