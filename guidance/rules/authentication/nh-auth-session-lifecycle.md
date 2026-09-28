---
id: nh-auth-session-lifecycle
title: "Authentication session lifecycle"
area: authentication
reference: authentication-session-lifecycle
summary: "Keep refresh tokens independent per login, keep the session validator startup-safe, revoke the current token on logout, and invalidate every session after a password mutation without expiring existing sessions during adoption."
sample-cases: ["SPM-064", "SPM-066", "SPM-075"]
public-symbols: ["LogoutRequest", "INhAuthenticationSessionValidator", "NhAuthenticationService", "NhUserManager", "BaseNhAuthService"]
skills: ["newheap-authentication"]
providers: ["sqlserver", "postgresql"]
risk: critical
---
## Preferred approach

Treat every successful login as an independent refresh-token session. Rotate only the presented refresh token in one transaction, and reject a second attempt to consume the same token without touching tokens from other devices or browsers. Send the current refresh token in `LogoutRequest`; the standard logout endpoint falls back to the configured HttpOnly refresh-token cookie for older frontends and revokes only that exact token.

Use the standard `NhUserManager` password change and reset methods. A successful password mutation records the current Identity security stamp as a compatibility marker, deletes every refresh token for the user, and causes stamped access tokens to fail validation. Access tokens issued before this behavior remain valid during rollout until that user's first password mutation, so upgrading does not create a mass logout or require a data backfill.

When a derived user manager has consumer-specific password models or additional atomic updates, run its Identity mutation through `ExecutePasswordMutationWithSessionInvalidationAsync`; do not call the Identity password APIs directly. For a PIN, passkey or other credential, derive `NhAuthenticationService`, record a failed Identity attempt when verification fails, and call `CreateAuthenticationSessionAsync` after verification succeeds. That extension point enforces account eligibility and lockout, resets an existing failed-attempt count, checks required claims, preserves every other device session and leaves the password unchanged.

Configure consumer JWT events through `NewHeapAspNetCommonOptionsBuilder.ConfigureJwtBearer` or compose with the existing `JwtBearerOptions.Events` delegates. NewHeap invokes consumer `OnTokenValidated` behavior and then enforces its session check. Code that validates tokens outside ASP.NET's bearer pipeline must also call the registered `INhAuthenticationSessionValidator` after cryptographic validation.

Keep the platform-owned session-validator registration intact. It is constructed through the NewHeap registration factory so Development hosts and hosts that explicitly enable `ValidateOnBuild` can validate the container without consumers depending on internal implementation details.

Deploy backend versions across the cluster before relying on immediate access-token invalidation: an older node does not validate the security-stamp claim. Old and new frontends remain compatible because the logout body is optional and cookie fallback is retained.

## Avoid

- Replacing a user's single refresh token when another device logs in.
- Deleting all refresh tokens during ordinary logout.
- Backfilling the compatibility marker during deployment, which would invalidate every legacy access token.
- Treating a failed password mutation as successful or revoking sessions before the password change commits.
- Changing the user's password as an implementation detail of PIN or alternative-credential authentication.
- Replacing the complete `JwtBearerEvents` object in post-configuration without forwarding the existing delegates.
- Replacing the platform session-validator registration with reflection or another consumer-owned construction workaround.
- Treating `DecodeToken` or `ValidateToken` alone as session-aware authorization.
- Claiming cluster-wide access-token invalidation while old backend nodes still serve traffic.

## Verification

Build a Development `WebApplication` host with `ValidateOnBuild` enabled and the standard platform registration. Run the lifecycle test against SQL Server and PostgreSQL. Prove that two device tokens refresh independently, one token can be consumed only once, logout is idempotent and token-specific, failed password changes preserve sessions, and every successful standard or derived change/reset path removes all refresh tokens. Also verify that legacy access tokens are accepted before the compatibility marker exists and rejected afterward, while a token carrying the current security stamp succeeds. Exercise a verified custom credential without a password mutation and prove that it resets a prior failed-attempt count while an existing device refresh token remains usable. Verify that configured JWT event delegates remain registered and that manual token consumers call `INhAuthenticationSessionValidator`.
