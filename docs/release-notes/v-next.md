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

## NewHeap.Platform.AspNet.Proxy: host-relative redirect destinations stay on this origin

A redirect destination that starts with `/` is now validated as the path the `Location` header
carries, after dot segments (`..`, `.`, `%2e%2e`, `%2e`) are removed. This applies when a rule is
saved or activated and to every regex expansion. A capture with dot segments can no longer turn a
host-relative destination into the network path `//host`: under `^/old-projects/([^?]+)(\?.*)?$`
with `/projects/$1$2`, a request for `/old-projects/%252e%252e//host` reaches the pattern as
`%2e%2e//host` and expanded to `/projects/%2e%2e//host`, which redirected to `//host`. Such an
expansion now skips redirect evaluation and the request passes through, as other unsafe expansions do.

| Breaking change | Required action |
|---|---|
| An exact target or regex template that resolves to `//host`, such as `/a/..//host`, now fails validation; a snapshot with one no longer saves or activates. | Use the host's absolute URL, or a path that resolves to one leading `/`. |
