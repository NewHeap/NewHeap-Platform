---
id: nh-auth-token-signing
title: "Access token signing and key rotation"
area: authentication
reference: authentication-session-lifecycle
summary: "Keep the shared HS256 key for a self-contained application, and sign with RS256 or ES256 from a private key in the secrets file when other hosts validate its tokens: publish the public keys with MapNewHeapJwks, validate elsewhere with UseNewHeapJwks, rotate through previous keys and bind tokens for another service to it with a real aud."
sample-cases: ["SPM-251"]
public-symbols: ["NhJwtSigningKeys", "NhJwtSigningKeySet", "NhJwtSigningAlgorithms", "NhJwtSigningKey", "MapNewHeapJwks", "UseNewHeapJwks", "NhJwksEndpointRouteBuilderExtensions", "NhJwksJwtBearerOptionsExtensions", "CreateDelegatedTokenAsync", "GetSigningCredentials", "CreateTokenForAudienceAsync"]
skills: ["newheap-authentication"]
providers: ["provider-neutral"]
risk: critical
---
## Preferred approach

An application whose tokens only it validates keeps the default: `HS256` with the shared `NhJwtSigningKey` (`NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key`, at least 32 bytes). Nothing changes for it. A host that can validate an HS256 token can also mint one, so the moment another host must validate the application's tokens, such as a first-party MCP server that receives the assistant's delegated tokens, sign asymmetrically instead.

Configure `NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Signing` (`NhJwtSigningKeys`): `Algorithm` `ES256` (a P-256 key) or `RS256` (RSA, at least 2048 bits), a `KeyId`, and exactly one private key in the secrets file, read with `NhConfigurationSecrets.Read`: `PrivateKey` as PEM, or `Pkcs12` as Base64 with an optional `Pkcs12Password`. The authentication service signs with it and sets the `kid`; the platform's validation parameters accept the current public key and every `PreviousKeys` entry (`KeyId` and `PublicKey`, a PEM public key or certificate) with `ValidAlgorithms` limited to their algorithms. Map `app.MapNewHeapJwks()` (anonymous `GET /.well-known/jwks.json`, cached for five minutes) to publish the public keys; it never contains a private or shared key, and mapping it for an HS256 application fails at startup.

Another host validates with `AddJwtBearer(options => options.UseNewHeapJwks(jwksUri, issuer, audiences))`: issuer, audiences, RS256 or ES256 only, the key set read every hour and again, at most once a minute, when a token names an unknown key id. It holds no key that could mint a token.

Rotate without a restart of the receiving hosts: create the new key, make it the current `PrivateKey` and `KeyId`, and move the old key's public key into `PreviousKeys`; remove it once the longest-lived token signed with it (the access token lifetime) expired. The issuing host validates its own tokens with the keys it read at startup, so restart its instances after changing the keys. Stored refresh tokens are keyed with the shared key unless `NhRefreshTokenKey` (`NewHeap:PlatformAspNetCommon:Authorization:RefreshToken:Key`) is set, so an application that signs asymmetrically without a shared key sets it, and one that keeps HS256 sets it before rotating the shared key when refresh sessions must survive the rotation. While an application moves from HS256, `AcceptSharedKeyTokens: true` keeps accepting tokens of its shared key on the issuing host itself for one access token lifetime; the shared key is never published.

A token for another service carries that service as `aud`: `INhAuthenticationService.CreateDelegatedTokenAsync(userId, claims, expiration, audience)` sets `audience` when the application signs asymmetrically, so only that service accepts it and the issuing application, which expects its issuer as audience, refuses it. With HS256 it keeps the issuer as audience, as hosts that share the key expect. Derived services customize signing through `GetSigningCredentials` and audience-bound tokens through `CreateTokenForAudienceAsync`.

## Avoid

- Sharing the HS256 key with a host that only needs to validate tokens; it can mint tokens for every person and claim.
- Committing a private key to source control or appsettings; keep it in the secrets file or a vault-backed configuration provider.
- Reading the key with `configuration[key]`, which turns a missing secret into its `${Secrets:…}` reference text.
- Reusing a `kid` for a new key, or removing a previous key while tokens signed with it are still valid.
- Leaving `AcceptSharedKeyTokens` on after the move: the old shared key keeps minting accepted tokens.
- Validating another application's tokens without an audience, or accepting HS256 next to the published keys on a receiving host.

## Verification

Test that an application without the signing section signs and validates HS256 exactly as before, that RS256 and ES256 sign with a PEM or PKCS#12 key from the secrets file and set the `kid`, that a token of a previous key validates until the key is removed, and that configuration errors (unknown algorithm, missing key id, both or neither key, a public key, another curve, a small RSA key, a repeated key id) stop the platform with a message that names the setting and never the key. Test that the key set contains only `kty`, `use`, `kid`, `alg` and public parameters, that another host validates with it, refuses another audience, issuer or key, and picks up a rotated key, and that a delegated token for a service carries the service as `aud` with asymmetric signing and the issuer with HS256.
