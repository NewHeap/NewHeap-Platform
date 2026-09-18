# v-next

<!-- AI assistant and API bridge lanes: each lane writes only below its own heading. The coordinator regroups these sections into the per-package format before merge. -->

## Lane A

## Lane B

## Lane C

### @newheap/platform-ai-chat

New package. Angular assistant panel (`provideNhAssistant`, `nh-assistant-launcher`, `nh-assistant-panel` and building blocks) for the assistant API, with a scripted mock API in `@newheap/platform-ai-chat/testing`. Peer dependencies: Angular 20.3, `@angular/cdk` 20.2, `@ngx-translate/core` 17, `marked` 18, `dompurify` 3.4. Hosts load `@angular/cdk/overlay-prebuilt.css`. No breaking changes; existing packages are unaffected.
