# v-next

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: turn data that does not fit

The model instructions of a turn are at most 32,768 characters. A turn whose instructions, application
map, turn data and retrieved context went over that ran on its instructions alone, without a log, and
its audit named a block the model never received (FB72). One fit step now keeps what fits. The
lowest-ranked passages go first, one at a time. The application map goes only when the text does not
fit without any passage, and the passages that fit in its room come back. The turn data stays as long
as it fits, and only then does the turn run on its instructions alone. When passages were left out,
the block says how many in the turn's language, so the model does not conclude that the sources are
silent. A warning names the agent, the turn, the passages kept and dropped and the characters, never
content, and startup warns about an agent or skill in code whose instructions plus retrieval budget
exceed the limit. The note about missing MCP connections keeps its room next to the block.

| Breaking change | Required action |
|---|---|
| `RetrievedContextHash` and `ApplicationMapHash` of `NhAssistantAuditEvent` are those of what the model received, and empty when nothing was sent. A turn that left passages out records another hash than the trace of its resolution. | Treat an empty hash as "not sent". Compare audit hashes with the `contextHash` of the block, not with the trace. |
| `ContextFactCount`, `PageEntityCount` and `HadPageContext` are 0 and `false` for a turn that ran on its instructions alone. | Read them as what the model received. |

## NewHeap.Platform.AI.Common and NewHeap.Platform.AI.Chat: a context budget that counts the formatted block

`MaxRetrievedCharacters` and `UseRetrievedContext`'s `MaxCharacters` now limit what the model receives,
whichever `INhAiContextFormatter` is registered (FB73): the whole `# Retrieved context` block counts,
and the lowest-ranked passages beyond it are left out with a note. The default formatter leaves out
empty fields and escapes only what JSON requires, so Dutch text and quotes in `content` read as they
are; a character outside the Basic Multilingual Plane, such as an emoji, stays a pair of escapes.

| Breaking change | Required action |
|---|---|
| `MaxCharacters` and `MaxRetrievedCharacters` count the formatted block, so a full budget holds fewer passages. The defaults stay. | When answers miss passages, raise both limits (at most 24,000). With the default formatter, add about 300 characters for the block and 160 per passage, plus its ids and provenance. |
| The default `INhAiContextFormatter` leaves out `provenance`, `content`, `contentReference` and `contentHash` when they are empty, and no longer escapes `<`, `>`, `&`, `'`, `+` and text outside ASCII. | Treat a missing field as empty when you read the JSON. Neutralize markup before you place `FormatAsData` output between delimiters of your own. |
| `INhAiPromptAssembler`'s default retrieved block replaces `<` and `>` with `‹` and `›`, for any formatter. | If a formatter of yours writes markup for this block, replace the assembler with `UsePromptAssembler`. |
