# v-next

## NewHeap.Platform.AI.Chat.AspNet: a host with only chat-only agents follows the run configuration

This adds to the 22.0.0 note on chat-only agents (`run.ChatOnly()`); nothing changes. A host whose only
agents with run settings are chat-only still loads the run configuration defined as data when it
starts and checks its stamp every 30 seconds (`NhAssistantRunConfigurationRefresh`, a hosted service,
not a background operation). A chat turn does not check the stamp, so this is how the agent's skills
defined as data come into effect and follow changes made on another host.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AspNet.Common: refresh tokens stored as keyed digests

A refresh-token row (`NhUserAuthRefreshToken.Token`) now holds `nhr1:` and the HMAC-SHA256 of the token
instead of the token itself; only the client receives the token. The key comes from the new optional
secret `NewHeap:PlatformAspNetCommon:Authorization:RefreshToken:Key` (`NhRefreshTokenKey`), or else from
the shared JWT signing key. Refresh and logout look the row up by the digest of the presented token, so
a copy of the table cannot be presented as a refresh token. A row stored before the upgrade holds the
token itself and no longer matches; nothing falls back to it. The column keeps its type, since the
digest has 69 characters, so no migration is needed.

| Breaking change | Required action |
|---|---|
| Refresh tokens issued before the upgrade, or by a node that still runs the previous version, no longer refresh; each device signs in again once. | None. Optionally delete the `NhUserAuthRefreshToken` rows whose `Token` does not start with `nhr1:` once every node runs this version. |
| Signing in needs a key for the digest: an application that signs asymmetrically without the shared `JWT:Token:Key` fails with a configuration error. | Set `NewHeap:PlatformAspNetCommon:Authorization:RefreshToken:Key`, a random secret of at least 32 bytes, in the secrets file. |
| Changing the key in use ends every refresh session: the refresh-token key, or the shared signing key while no refresh-token key is set. | Set `RefreshToken:Key` before you rotate the shared signing key, so refresh sessions survive the rotation. |

## NewHeap.Platform.AspNet.Common: refresh refuses accounts that may not sign in

`AuthenticateRefreshTokenAsync`, behind `POST authentication/refresh`, now refuses a user whom Identity
does not let sign in (`SignInManager.CanSignInAsync`) or who is locked out or blocked
(`INhUserManager<TUser>.IsBlockedAsync`, which also sees a `LockoutStart` without an end or on an
account with lockout disabled). The check runs before the token is consumed and fails with the same
`Invalid refresh token` as an unknown token, so the response says nothing about the account. The token
stays stored and refreshes again once the account may sign in.

| Breaking change | Required action |
|---|---|
| A locked-out, blocked or otherwise not permitted account no longer refreshes; before, the refresh rotated its token and issued a new session. | None. To end the account's refresh sessions for good, also reset its password, which revokes its refresh tokens. |

## NewHeap.Platform.AspNet.Common: every sign-in path refuses blocked accounts

`NhAuthenticationService` now applies one eligibility check to every first factor and every step that
completes a sign-in: Identity must let the user sign in (`SignInManager.CanSignInAsync`), and the account
may be neither locked out nor blocked (`INhUserManager<TUser>.IsBlockedAsync`). Before, these paths saw
only Identity's lockout, so an account blocked through `UpdateUserLockout` without an end date, or on an
account with lockout disabled, still received a session. The check covers the password with and without
two-factor authentication, passkey sign-in, consumer credentials through `CreateAuthenticationSessionAsync`
and `CompleteFirstFactorAsync`, external providers, second-factor challenges, enrollment during sign-in
and refresh. The password is not checked for a blocked account, so a wrong one no longer counts as a
failed attempt.

| Breaking change | Required action |
|---|---|
| A blocked account receives `User locked out` on a first factor and `two-factor-locked-out` on a pending challenge or enrollment, instead of a session. | None. |
