# v-next

## NewHeap.Platform.AI.AspNet.Common and NewHeap.Platform.AI.AspNet.Mvc: gateway reads say why they fail (FB96)

- A gateway read of a headless agent run that gets no usable delegated read credential fails with its cause as
  the code (`NhAiDelegatedReadFailureCodes`), which the model, the run's tool call, the administration and the
  agent builder show:
  - `ai-delegated-read-target-unknown`: the host authenticated the credential before routing knew its target.
    Call `UseRouting` before `UseAuthentication`; with `UseNewHeapPlatformAspNetCommon`, route controllers with
    `UseEndpointRoutingOnly()`. Under `UseMvc` the bridge's MVC filter authenticates the scheme with the
    executing action as its target (`AuthenticateForTargetAsync`), so no middleware may authenticate it first,
    as it does for the default authentication scheme.
  - `ai-delegated-read-no-issuer`: `AddNewHeapPlatformAIDelegatedReads` is missing, or its built-in issuer runs
    on a host without `INhAuthenticationService`; register an issuer with `UseIssuer<T>()`.
  - `ai-delegated-read-issuer-refused`: the issuer refused the person, failed, or returned a token without
    exactly its binding claims or that lives too long.
  - `ai-delegated-read-rejected`: the host's bearer scheme rejected the token, or the API answered the read
    with `401` for another reason.
- Each cause logs one content-free warning per host that names the fix (events 6 to 9 of
  `NhAiDelegatedReadService`); the warnings of each occurrence (events 1 to 5) stay. The host names an unknown
  target for a call it answered itself; a call another instance answered fails as `ai-delegated-read-rejected`.
- A person who may not lend rights now (no fresh context under `UsePrincipalsFromAccessSource`, or a subject's
  responsible person who changed after the run was queued) still fails with `api-bridge-unauthenticated`.

| Breaking change | Required action |
|---|---|
| A gateway read of a headless run that gets no usable delegated read credential fails with its cause (`NhAiDelegatedReadFailureCodes`) instead of `api-bridge-unauthenticated`; a locked-out or removed person now gets `ai-delegated-read-issuer-refused`. | Where you match `api-bridge-unauthenticated` for gateway reads, also match `NhAiDelegatedReadFailureCodes.All`. |

## @newheap/platform-ai-chat: why a gateway read failed (FB96)

- The causes of a gateway read that got no usable delegated read credential (`ai-delegated-read-target-unknown`,
  `ai-delegated-read-no-issuer`, `ai-delegated-read-issuer-refused` and `ai-delegated-read-rejected`) have a text
  in English and Dutch under `nh-assistant.errors`, so a tool call, a rule script's call and a run say what
  happened and who fixes it.
