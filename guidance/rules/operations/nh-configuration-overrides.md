---
id: nh-configuration-overrides
title: "Configuration overrides for runtime and automation"
area: configuration
reference: operations
summary: "Use the same appsettings and NewHeap secrets files on every host while allowing environment variables and explicitly supplied CLI arguments to override host-specific values."
sample-cases: ["SPM-096"]
public-symbols: ["ConfigureNhCommonConfiguration", "UseNhCommonConfiguration", "UseNewHeapAspnetCommonConfiguration", "CreateConfigurationRoot"]
skills: ["newheap-consumer-development"]
providers: ["provider-neutral"]
risk: medium
---
## Preferred approach

Pass the application arguments to `UseNewHeapAspnetCommonConfiguration(args)` or `ConfigureNhCommonConfiguration(args)`. The library uses environment variables and CLI arguments both while resolving `NewHeap:PlatformCommon:AppSecretsDirectoryPath` and in the final configuration. Precedence is appsettings and NewHeap secrets files, then environment variables, and finally CLI arguments.

In pipelines, prefer environment variables with double underscores, such as `NewHeap__PlatformCommon__AppSecretsDirectoryPath` or `ConnectionStrings__DefaultConnection`. Use CLI arguments mainly for non-secret host settings. A design-time `NhDbContextFactory` forwards its `CreateDbContext(string[] args)` arguments to `CreateConfigurationRoot(args)`.

## Avoid

- Forcing a Windows runner to use the Linux secrets path from `appsettings.Production.json`.
- Passing production secrets as CLI arguments when process lists or logs can expose them.
- Treating `AddUserSecrets` as a production or pipeline provider; user secrets are a local development facility.
- Removing or changing existing overloads; consumers must remain binary compatible.

## Verification

Test that an environment variable overrides the secrets directory during bootstrap, that the selected secrets file is actually loaded, and that the same environment value is present in the final configuration. Separately test that CLI arguments take precedence over environment variables and that configuration without overrides retains the existing file behavior.
