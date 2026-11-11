/*
 * Public API surface of @newheap/platform-ai-chat/client-tools: the built-in handlers of client tools,
 * which navigate within the application or open its own mutate modal. They live in an entry point of
 * their own because the modal handler uses the modal service of @newheap/platform-common.
 */
export * from './nh-assistant-navigate-tool';
export * from './nh-assistant-modal-tool';
