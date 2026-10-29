# v-next

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
