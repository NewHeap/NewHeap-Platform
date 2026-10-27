---
id: nh-auth-microsoft-oauth
title: "Microsoft OAuth sign-in"
area: authentication
reference: authentication-session-lifecycle
summary: "Let a Microsoft sign-in complete only in the browser that started it, with a protected, single-use, short-lived state and PKCE, and return only to a path of the application or an allowed origin; configure the callback on the host where the sign-in starts, and test with NhMicrosoftOAuthTestPlatform instead of Microsoft."
sample-cases: ["SPM-062"]
public-symbols: ["AddMicrosoftOAuth", "AddMicrosoftOauthEndpoints", "MicrosoftAuthSettings", "MicrosoftAuthService", "NhReturnUrls", "NhMicrosoftOAuthTestPlatform"]
skills: ["newheap-authentication"]
providers: ["sqlserver", "postgresql"]
risk: critical
---
## Preferred approach

Enable Microsoft sign-in with `AddMicrosoftOAuth(oauth => ...)` on the authentication builder and map `AddMicrosoftOauthEndpoints()` in `UseNhAuthentication(...)`. The browser starts a sign-in with `POST authentication/oath/microsoft` (`callbackUrl` is where the browser returns, `userName` an account in `AuthDomains`), opens the returned Microsoft URL, and Microsoft sends it to `GET authentication/oath/microsoft/authorize`, the `CallbackUrl` registered as the app registration's redirect URI.

The start endpoint issues the OAuth state as a protected flow (ASP.NET Core Data Protection): its return URL, a nonce and a PKCE code verifier, valid for ten minutes. It binds the flow to the browser with an HttpOnly, Secure, `SameSite=Lax` correlation cookie for the path of `CallbackUrl`, and sends Microsoft only the S256 challenge of the verifier. The callback accepts only a state the application issued, unchanged and unexpired, from the browser that carries its cookie, removes the cookie so the state completes once, and redeems the code with the verifier. A callback from another browser, such as a login CSRF that carries the attacker's code and state, or with a state the application did not issue, answers 400 and sets no session cookie.

The return URL must pass `NhReturnUrls.IsAllowed`: a path on the callback's host, such as `/projects`, or an absolute `http` or `https` URL whose origin is in `MicrosoftAuthSettings.AllowedReturnOrigins`, or in `NewHeapAspNetCommonSettings.AllowedOrigins` when that list is empty. Network paths (`//host`), backslashes, user information and other schemes are refused. The start endpoint checks it before it issues a state, and the callback again before it redirects; a pending second-factor step travels only in the fragment of that allowed URL. Use `NhReturnUrls.IsAllowed` for other redirects that take a client-supplied target.

Serve the start endpoint and the callback from the same host as the browser sees it, so the cookie reaches the callback: an Angular app that calls the API through an `/api` proxy registers the callback behind that proxy, as the sample does with `http://localhost:4210/api/authentication/oath/microsoft/authorize`. An application with several instances shares the Data Protection key ring, as two-factor tickets need too.

Test Microsoft sign-in without Microsoft: make `NhMicrosoftOAuthTestPlatform` from `NewHeap.Platform.AspNet.Common.Test` the primary handler of the `MicrosoftAuthService` client. `SignIn(authorizationUrl, account)` returns the code for the URL the start endpoint returned; the stand-in redeems a code once and only with the PKCE verifier, and returns an ID token with the account's tenant, object id and the request's nonce.

## Avoid

- Building the OAuth state yourself from the return URL and a random value, or redeeming a code without the flow's verifier; `MicrosoftAuthService.GetLoginUrl(state)` and `GetToken(code, state)` are obsolete for that reason.
- Redirecting to a return URL that a client supplied without `NhReturnUrls.IsAllowed`, or allowing every origin.
- Registering the callback on the API host while the browser starts the sign-in through a proxy on another host; the correlation cookie then never reaches the callback.
- Calling Microsoft's real endpoints from tests.

## Verification

Run the Microsoft OAuth tests against SQL Server and PostgreSQL with `NhMicrosoftOAuthTestPlatform`. Prove that a sign-in completes once in the browser that started it and returns to the allowed URL with the session cookies; that the correlation cookie is host-only, HttpOnly, Secure and `SameSite=Lax` for the callback's path and is removed by the callback; that a callback without the cookie, with a state the application did not issue, a changed state or a state older than ten minutes on the application's clock answers 400 without a session cookie; and that a return URL on another origin, a network path, a backslash path, user information or another scheme starts no sign-in, while a path of the application does.
