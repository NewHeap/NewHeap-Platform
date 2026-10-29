# v-next

## NewHeap.Platform.AI.Generators and NewHeap.Platform.AI.Scripting: DateOnly and TimeOnly in tool schemas

The tool generator described a `DateOnly` and a `TimeOnly` as objects of their public properties, all
required (`{ day; dayNumber; dayOfWeek; dayOfYear; month; year }`), while System.Text.Json reads and
writes them as `"2026-11-01"` and `"14:30:00"`. The model was shown a shape the tool could not bind, and
a script's call check refused every date the tool accepts. A generated schema now has
`{"type":"string","format":"date"}` for a `DateOnly` and `{"type":"string","format":"time"}` for a
`TimeOnly`, nullable, in lists and in nested types included, as the MVC bridge and composed tools already
had. A script sees them as `string /* date */` and `string /* time */`, and its call check accepts only
`yyyy-MM-dd` for a `date` and `HH:mm[:ss[.fffffff]]` without an offset for a `time`; any other string
stops the script with `ai-script-call-invalid` at that field before the tool runs. SPM-375 shows it in the
sample.

| Breaking change | Required action |
|---|---|
| A generated tool with a `DateOnly` or `TimeOnly` in its input or output has a new schema, schema hash and contract hash after the rebuild, so a proposal of that tool recorded before the deployment no longer runs when it is approved (`ai-tool-approval-invalid` or `assistant-approval-invalid`). | Decide the open proposals of such tools before you deploy, or let the model propose them again afterwards; update a manifest or contract hash the application pins itself. |
| An application that imports such a tool over MCP sees it disabled as `schema-changed` once the exporting application is deployed, because its reviewed schema changed. | An administrator reviews the tool again and activates it in the assistant's MCP administration. |
| A script's call check now refuses a `date` or `time` string in another form, also for an imported MCP tool whose schema declares these formats; a `time` with an offset such as `14:30:00Z` is refused. | Pass `yyyy-MM-dd` and `HH:mm[:ss[.fffffff]]`; a script that passed another form stops at that field and the model corrects it. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the page a person has open from a channel, and the HTTP status of an assistant code

`NhAssistantConversationMessage.ClientContext` carries the page a person has open, such as the page
around a website widget, to a turn through `INhAssistantConversationTurns.SendAsync`, in the endpoint's
`NhAssistantClientContextDto` (FB52). The service reads it as `POST conversations/{id}/messages` reads its
`clientContext`, so the turn shows the same "User's screen", the message stores the same context and a
turn that resumes after an approval or a question shows it again. Where the endpoint ignores what it cannot
read, the service refuses with `assistant-message-invalid` a page the endpoint would not keep whole: a route
without text, more than five entities, an entity that is `null`, whose type is not dash-case or whose id is
empty, longer than 64 characters or holds a control character, and a page whose stored form exceeds the
4,000 characters the message keeps for it. Long text is cut off as the endpoint does (SPM-380).

The stored context now keeps text outside ASCII as it is, and still escapes HTML-sensitive characters
such as `&` and characters outside the Basic Multilingual Plane such as emoji. A valid page at every
limit in any language fits the 4,000-character `ClientContextJson` column; with every character outside
ASCII escaped, such a page could exceed it, and `POST conversations/{id}/messages` then failed when it
stored the message. A page that still would not fit, such as `&` or emoji in nearly every field at its
limit, is ignored as a context that cannot be read. Contexts stored before read back unchanged.

`NhAssistantHttpStatuses.Of(code)` answers the HTTP status the assistant's endpoints give a code, and
`NhAssistantHttpStatuses.UnknownCodeStatus` (`400`) for a code the assistant does not know (FB53). The
endpoints' error responses use it themselves, so an application that serves a channel through its own API
answers a failed service result as the endpoints answer the same code (SPM-381).

| Breaking change | Required action |
|---|---|
| `NhAssistantConversationMessage` has the new optional `ClientContext`, and `NhAssistantHttpStatuses` is new; a message without a page and every status of the endpoints stay as they were. | None. A channel that passes the page through a turn context provider of its own may pass it as `ClientContext` instead, and a channel that keeps its own table of statuses may answer with `NhAssistantHttpStatuses.Of`. |
| `POST conversations/{id}/messages` no longer fails to store a message whose `clientContext` has text outside ASCII near every limit: the stored context keeps such text as it is, and a context that still would not fit the 4,000-character column is ignored like an unreadable one. | None; a client that sends such pages gets them shown to the model, or ignored, instead of a failed message. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: approver candidates the application names

An application that knows who passes a policy rule (`NhAiApprovers.Policy`) names those people with an
`INhAssistantApproverCandidates`, registered with `UseApproverCandidates<TCandidates>()`, so
`INhAssistantApproverDirectory` can answer `Complete: true` where it could not before: a policy rule admits
anyone who passes it, and the whole application lists no members. The directory asks it only when people
outside the members may approve and the members did not fill the limit, with an
`NhAssistantApproverCandidateQuery` (the policy, the subject, the subject's tenant and division, the
`PolicyNames` of the policy's rules, `SubjectRoleHolders` for a subject without listed members, and
`MaxCandidates`). It checks each `NhAssistantApproverCandidate` exactly as it checks a member, the rebuilt
principal, roles, the tool's approve policies and four eyes included, lists those who pass after the members,
and answers complete only when the application declared its `NhAssistantApproverCandidateList` complete and
neither the limit nor `MaxCandidates` applied. A candidates service that throws leaves the list incomplete.
Without a registration nothing changes (SPM-378).

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the recorded policy for guards and the executor

`NhAiProposalGuardContext` and `NhAssistantProposalHandOff` have init-only `PolicyId` and `Mode`
(`NhAiApprovalMode`): the approval policy a proposal waits under and the mode it got, as the proposal
recorded them, also a policy an approval policy selector chose. Guards read them at revalidation, decision
and execution; they are `null` when a call proposes and for the keys of a new proposal, because the policy
is chosen after the guards ran. A proposal in a person's own chat has no policy (`PolicyId` `null`, `Mode`
`Inline`). An application no longer reads them from the assistant's tables to check whether what decided a
selector's choice still holds (SPM-379). No migration: the approval already stores both.

| Breaking change | Required action |
|---|---|
| `NhAiProposalGuardContext` and `NhAssistantProposalHandOff` record equality now also compares `PolicyId` and `Mode`, which revalidation, decision, execution and every hand-off fill. | Compare the members you mean, such as `ProposalId` or `ApprovalId`, instead of whole records in a cache key or a test, or set `PolicyId` and `Mode` in the expected record. |
