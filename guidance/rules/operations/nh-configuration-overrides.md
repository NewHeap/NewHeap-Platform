---
id: nh-configuration-overrides
title: "Configuration overrides for runtime and automation"
area: configuration
reference: runtime-configuration
summary: "Use the same appsettings and NewHeap secrets files on every host while allowing environment variables and explicitly supplied CLI arguments to override host-specific values."
sample-cases: ["SPM-096"]
public-symbols: ["ConfigureNhCommonConfiguration", "UseNhCommonConfiguration", "UseNewHeapAspnetCommonConfiguration", "CreateConfigurationRoot", "NhConfigurationSecrets", "NhJwtSigningKey"]
skills: ["newheap-runtime-configuration"]
providers: ["provider-neutral"]
risk: medium
---
## Preferred approach

Pass the application arguments to `UseNewHeapAspnetCommonConfiguration(args)` or `ConfigureNhCommonConfiguration(args)`. The library uses environment variables and CLI arguments both while resolving `NewHeap:PlatformCommon:AppSecretsDirectoryPath` and in the final configuration. Precedence is appsettings and NewHeap secrets files, then environment variables, and finally CLI arguments.

Secrets live in the NewHeap secrets files, which the configuration loads under the `Secrets` prefix; appsettings refer to them with `${Secrets:…}`. A reference whose secret is missing stays as literal text that anyone can read in appsettings, so never use a configured secret directly: read it with `NhConfigurationSecrets.Read(configuration, key)`, which takes the key itself (appsettings, environment variable, command line) or else `Secrets:{key}`, and returns `null` for a blank value or an unresolved reference. At startup the common platform warns with the names, never the values, of every setting that still holds an unresolved reference. The JWT signing key fails closed: `NhJwtSigningKey` refuses a missing key, an unresolved reference and a key shorter than 32 bytes, so `AddNewHeapPlatformAspNetCommon` stops the application instead of signing tokens with a known value; the assistant's webhook triggers refuse such a secret as well.

In pipelines, prefer environment variables with double underscores, such as `NewHeap__PlatformCommon__AppSecretsDirectoryPath` or `ConnectionStrings__DefaultConnection`. Use CLI arguments mainly for non-secret host settings. A design-time `NhDbContextFactory` forwards its `CreateDbContext(string[] args)` arguments to `CreateConfigurationRoot(args)`.

## Avoid

- Forcing a Windows runner to use the Linux secrets path from `appsettings.Production.json`.
- Passing production secrets as CLI arguments when process lists or logs can expose them.
- Reading a secret with `configuration[key]` and using it as a signing key, webhook secret or password without `NhConfigurationSecrets.Read`: a missing secret then becomes its own `${Secrets:…}` reference text.
- Treating `AddUserSecrets` as a production or pipeline provider; user secrets are a local development facility.
- Removing or changing existing overloads; consumers must remain binary compatible.

## Verification

Test that an environment variable overrides the secrets directory during bootstrap, that the selected secrets file is actually loaded, and that the same environment value is present in the final configuration. Separately test that CLI arguments take precedence over environment variables and that configuration without overrides retains the existing file behavior. Test that a missing secret leaves its reference as text, that `NhConfigurationSecrets.Read` treats it as missing and `FindUnresolvedReferences` names its key, and that the platform refuses a missing, unresolved or too short JWT signing key.
