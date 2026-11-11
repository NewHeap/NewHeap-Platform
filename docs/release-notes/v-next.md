# v-next

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: agents keep a visible plan

| Breaking change | Required action |
|---|---|
| The `AgentRunSettingsKeepsPlan` migrations add the column `KeepsPlan` (not null, default `false`) to `AssistantAgentRunConfiguration` on SQL Server and PostgreSQL. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. |
