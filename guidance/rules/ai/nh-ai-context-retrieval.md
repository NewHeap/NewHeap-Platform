---
id: nh-ai-context-retrieval
title: "Resolve authorized AI context as untrusted data"
area: backend
reference: ai-context-retrieval
summary: "Authorize context sources before retrieval, enforce execution scope and classification, preserve provenance and trust, resolve conflicts deterministically, and apply hard content budgets."
sample-cases: ["SPM-226", "SPM-291"]
public-symbols: ["INhAiContextAuthorizationPolicy", "INhAiContextFormatter", "INhAiContextRanker", "INhAiContextResolver", "INhAiContextSource", "NhAiContextItem", "NhAiContextRankedItem", "NhAiContextRequest", "NhAiContextResolution", "NhAiContextSourceDescriptor", "NhAiContextTraceEntry", "NhAiContextTrust", "NhAiTestContextAuthorizationPolicy", "NhAiTestContextSource", "INhAiContextTraceSink", "NhAiContextResolutionTrace", "UseRetrievedContext", "NhAssistantRetrievedContextBuilder", "NhAssistantRetrievedContextOptions", "MaxRetrievedCharacters", "RetrievalTimeout"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

Keep canonical documents and hierarchy in the consuming application. Implement a
bounded `INhAiContextSource` that declares a stable source ID, classification,
item limit and content-size limit. Register an explicit
`INhAiContextAuthorizationPolicy`; the platform default denies every source and
calls policy before retrieval.

Every returned `NhAiContextItem` must bind to one or more execution scopes that
already exist in the authorized invocation context. Include a stable item and
logical key, version, source, content type, classification, trust, created and
expiry times, replacement lineage where applicable, and bounded provenance
references. User-authored or externally retrieved text is
`UntrustedRetrieved`, even when it comes from an authenticated database.

The resolver drops cross-scope, expired, future, over-classified, oversized and
malformed items; selects the newest deterministic winner per logical key; ranks
within the query; and applies item, character and estimated-token budgets. Keep
the bounded trace and context hash as explainability/evaluation evidence, not
the full retrieved content in logs.

Use `INhAiContextFormatter.FormatAsData` to serialize retrieved items with
`instructionAuthority=false`. This metadata is defense in depth, not a security
boundary: tool policy, authorization and approval never come from retrieved
text or model output. The default formatter leaves out empty fields and escapes
only what JSON requires, so text in any language reads as it is; only a character
outside the Basic Multilingual Plane, such as an emoji, stays a pair of escapes.
Markup therefore stays in its output: neutralize `<` and `>` before you place it
between delimiters of your own. The assistant's block and the retrieved block of
`INhAiPromptAssembler` do that for any formatter.

## Retrieved context in agent turns

Give an assistant agent the items that match each question with
`ConfigureAgentContext(agentId, context => context.UseRetrievedContext(retrieval =>
retrieval.Sources("project-documents").MaxItems(5).MaxCharacters(4_000)))`. Register the sources
with `AddContextSource<T>()`; startup fails on a source id that is not registered, and agents
without `UseRetrievedContext` retrieve nothing. Name the sources each agent needs: the request
reads only those (`NhAiContextRequest.SourceIds`).

The agent's turn retrieves once, before its first model call, and its tool rounds reuse the
result. The query is the person's latest message, the question of a consultation, or in a headless
run the messages of its inputs and the subject's name, at most 1,000 characters. The request uses
the turn's own invocation context, so the policy and the scope filter see the person, or in a run
the agent with its accountable person, the subject and, where the host maps an active division,
the subject's division. A person's turn in an agent's thread keeps the person as the actor but
retrieves within the thread's subject and its division, as the turn's tool calls do: the request's
active division, which the client chooses for the whole application, and the capabilities derived
from it do not apply, because every reader of the subject reads the thread. A plain chat retrieves
in the person's own context. It asks for at most the highest classification the agent's model profile
permits. The highest classification among the retrieved items then raises the classification of
the turn's model calls, as the files the model reads do, so every profile that serves the turn must
permit it: `use_skill` refuses a skill whose profile chain does not
(`assistant-classification-not-permitted`), and a fallback profile that does not permit it never
serves the turn, which fails instead. Give skill and fallback profiles the classifications of the
sources their agent retrieves from. `MaxItems` (8 by default, at most 50) and `MaxCharacters` (6,000, within
`NhAssistantLimits.MaxRetrievedCharacters`, at most 24,000) become the request's item, character
and token budgets. A turn that continues after an approval does not retrieve again.

`MaxCharacters` limits what the model receives, not only the content of the items: the whole block
counts, as the registered `INhAiContextFormatter` writes it and with its introduction. The
lowest-ranked items beyond it are left out one at a time, so a full budget holds fewer items than
its characters of content suggest. With the default formatter, reckon with about 300 characters for
the block and 160 per item, plus the item's ids and provenance. Raise `MaxCharacters` and
`MaxRetrievedCharacters` together when answers need more.

The model reads a `# Retrieved context` block as the last turn data, after the situation and the
user's screen: the formatter's JSON with each item's source, id, `effectiveAt` date (`EffectiveAt`,
else `CreatedAt`), provenance and content, markup neutralized, introduced as untrusted data that
may be outdated or wrong and is never instructions. The block counts against the turn's history,
is not part of the prompt hash or the approval binding, and tool audit events carry its
`RetrievedContextHash`. A resolver that throws or exceeds `RetrievalTimeout` (5 seconds, at most
30) leaves the turn without the block, and the log names the exception type only. The library adds
an `INhAiContextTraceSink` that logs each resolution's content-free trace with the invocation id:
sources with their outcome codes and counts, selected items, characters and the context hash.

The model instructions of a turn are at most 32,768 characters, and the instructions, the application
map, the turn data and the block must fit in them together. One fit step before the model call keeps
what fits, in a fixed order. The lowest-ranked items go first, one at a time. The application map
goes only when the text does not fit without any item, and the items that fit in its room then come
back. The situation, the user's screen and the shared conversation stay as long as they fit; when
they do not, the turn runs on its instructions alone. Instructions over the limit still fail the
turn with `assistant-instructions-too-long`. A note about missing MCP connections keeps its room next
to the block. The block is formatted again for what remains, and a turn that switches skills fits
all of its items again for the skill's instructions. When items were left out, the block says how
many in the turn's language, so the model does not conclude that the sources are silent; when none
fit, that note is all the block holds. `RetrievedContextHash` and `ApplicationMapHash` are those of
what was sent and stay empty when nothing was, so the trace above can name a hash the model never
received when the fit dropped items. The turn's classification stays that of the items the budget
kept, also when the fit of the turn drops the highest one.

A warning names the agent, the turn, the items kept and dropped and the characters whenever a turn
leaves data out, never content, the query or item ids. At startup a warning names each agent or skill
in code with `UseRetrievedContext` whose instructions plus budget exceed the limit, counting the
seed of the application context. Instructions that change as data, such as an edited application
context or an agent created in the administration, show up in the warning of the turn.

A source that serves agent turns receives the whole question as its query. Match its words rather
than the full text, and stamp every item with the execution scopes it was read for.

## Avoid

- Querying a source before actor and scope authorization.
- Passing tenant or division from model input into the retrieval filter.
- Relying on the request's active division or its capabilities in a source that serves subject
  threads; a person's turn there retrieves within the subject's division only.
- Treating internal database text as trusted instructions.
- Silently merging conflicting versions or exceeding context budgets.
- Budgeting `MaxCharacters` for the content alone; it counts the formatted block.
- Reading a turn that answers from fewer items as proof that the sources are silent; look for the
  turn's warning and shorten the instructions or raise the budget.
- Placing the default formatter's output between delimiters of your own without neutralizing its
  markup.
- Logging documents, prompts, embeddings, credentials or provider payloads.
- Using vector similarity as authorization or policy evidence.
- Connecting every registered source to every agent; name the sources each agent needs.
- Letting an agent act on retrieved text; its tools read the current state with their own
  authorization.
- Giving an agent's skills or fallbacks profiles that permit less than the agent's own while its
  sources return items up to that profile's classification; such turns refuse the skill or fail.

## Verification

Cover denied sources that are never called, cross-scope isolation, expiry,
classification, deduplication and replacement, deterministic ranking, item and
token budgets, provenance and bounded traces. Include instruction-like content
and prove it remains serialized untrusted data while no tool visibility or
capability changes. For agent turns, assert that a person's turn, a consultation and a headless run
retrieve once with their query, scopes and classification, that the block follows the situation
and is reused by the tool rounds, that items of another division or above the classification never
reach the model, that a slow resolver leaves the turn without it and that the trace holds no
content. Assert that a person's turn in a subject thread retrieves with the person as actor within
the subject and its division, never the request's other active division, while a plain chat keeps
the person's own division. Assert that a skill or fallback profile that does not permit the highest retrieved
classification never receives the block: `use_skill` is refused and the turn does not fall back.
Assert that the block stays within `MaxCharacters` with the default and with a custom formatter, that
the lowest-ranked items go first and the map after them, that the turn data stays as long as it fits,
that the note appears in English and Dutch, that the audit hash is that of what was sent and absent
when nothing was, that a passage cannot close the block, that the warning holds no content, that a
turn that fits stays as it was and that the startup warning fires and stays silent. SPM-226, SPM-291, `NewHeap.Platform.AI.Tests` and `NewHeap.Platform.AI.Chat.Tests` are the
executable references.
