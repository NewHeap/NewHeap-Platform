---
name: newheap-runtime-configuration
description: Configure or troubleshoot NewHeap consumer appsettings, secrets directories, environment variables, command-line overrides and design-time configuration across local, hosted and CI environments.
---

# NewHeap runtime configuration

Read [runtime configuration](references/runtime-configuration.md) before changing NewHeap configuration composition or automation overrides.

Use the same appsettings and NewHeap secrets-file model on every host. Allow environment variables and explicitly supplied command-line arguments to override host-specific values with the supported precedence.

Prefer double-underscore environment variables for pipelines and avoid secrets in command-line arguments. Ensure design-time factories forward their arguments into the same configuration path as runtime hosts.

Verify secrets-directory resolution and final configuration independently, including environment-over-file and command-line-over-environment precedence.
