# v-next

## NewHeap.Platform.DatabaseRead.Tool: an untrusted TLS certificate, schema search on PostgreSQL and the verification check

A server certificate that the diagnostic host does not trust no longer reads as a transient network
failure. SQL Server `SEC_E_UNTRUSTED_ROOT` (`-2146893019`), `CERT_E_UNTRUSTEDROOT` and `CERT_E_CHAINING`,
and a certificate that .NET certificate validation rejects on either provider, return the new
classification `tls-certificate-untrusted`: not transient, with the new retry hint
`certificate-authority-trust-required`, stage `connection-open` and, on SQL Server, the SSPI code as
`providerCode`. The response still never contains the provider message. Npgsql does not say which check
failed, so on PostgreSQL a certificate rejected for its host name or validity period reports the same
classification; on SQL Server those codes remain `connection-failed`.

A PostgreSQL schema search without a schema, a search term or both failed with SQLSTATE `42P08`; the
omitted values are now bound as text. Permission verification, visibility and limits are unchanged.

Since 7.1.0, a rejected SQL Server principal reports the first rejected check as `error.verificationCheck`
next to `read-only-principal-not-verified`, on the CLI and in MCP results; this note was missing.
The function policy stays strict: SELECT or EXECUTE on any user-defined function rejects the principal
with `function-select` or `function-execute`.

| Breaking change | Required action |
|---|---|
| An untrusted server certificate returns classification `tls-certificate-untrusted`, not transient, with retry hint `certificate-authority-trust-required`, instead of the transient `connection-failed` with `network-access-required`. | Let code that maps classifications or retry hints handle values it does not know, do not retry this one, and trust the server's certificate authority on the diagnostic host. |
