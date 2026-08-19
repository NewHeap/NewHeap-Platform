# Codex instructions

The authoritative repository instructions for Codex are in [AGENTS.md](AGENTS.md).
Read and follow that file completely before changing code. In particular, every
public library change must keep `examples/SampleProjectManagement` in sync and
database work must follow the SQL Server/PostgreSQL provider matrix defined there.

Do not maintain separate Codex-only rules in this file; update `AGENTS.md` so all
coding agents receive the same policy. Consult its Angular lifecycle extension
point rules before changing pages, collections or modal content.

Use `skills/newheap-library-maintenance/SKILL.md` for public library work and
`skills/newheap-consumer-development/SKILL.md` for consuming applications.
