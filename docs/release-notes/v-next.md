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
