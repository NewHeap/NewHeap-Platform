import { NhAssistantMockRemoteTool, NhAssistantMockScenario } from '@newheap/platform-ai-chat/testing';

export const PROJECT_ASSISTANT_ID = 'sample-project-assistant';
export const ASSISTANT_PLAYGROUND_ADMIN_ROUTE = '/management/assistant/admin';
export const PLAYGROUND_MCP_SERVER_ID = 'sample-documents';

/** Remote tools of the simulated MCP server; the second list simulates a changed input schema. */
export const PLAYGROUND_REMOTE_TOOLS: NhAssistantMockRemoteTool[] = [
  { remoteName: 'searchDocuments', description: 'Searches project documents by keyword.', inputSchemaHash: 'search-v1', readOnlyHint: true },
  { remoteName: 'readDocument', description: 'Reads one project document.', inputSchemaHash: 'read-v1', readOnlyHint: true },
  { remoteName: 'archiveDocument', description: 'Archives a project document.', inputSchemaHash: 'archive-v1', readOnlyHint: false }
];

export const PLAYGROUND_CHANGED_REMOTE_TOOLS: NhAssistantMockRemoteTool[] = PLAYGROUND_REMOTE_TOOLS.map(tool =>
  tool.remoteName === 'searchDocuments' ? { ...tool, inputSchemaHash: 'search-v2' } : tool);
export const CATALOG_GUIDE_ID = 'sample-catalog-guide';

/** Prompts that exercise each scripted turn of the playground scenario. */
export const ASSISTANT_PLAYGROUND_PROMPTS = [
  { key: 'list', text: 'Which projects are active?' },
  { key: 'approval', text: 'Put project Alpha migration on hold.' },
  { key: 'forbidden', text: 'Delete the archived projects.' },
  { key: 'long', text: 'Write a long status report for all projects.' },
  { key: 'unsafe', text: 'Show me some HTML with a script tag.' },
  { key: 'error', text: 'Simulate an unavailable model.' }
] as const;

const longReport = [
  '## Portfolio status report',
  '',
  'Alpha migration is **active** and on schedule. The data export completed and the cut-over rehearsal is planned for next week.',
  '',
  'Beta onboarding is *on hold* while the customer confirms the contract scope. No work is scheduled until the scope is signed.',
  '',
  'Gamma analytics is **active**. The dashboard prototype passed review; the team now hardens the ingestion pipeline and adds monitoring.',
  '',
  '1. Confirm the Beta scope with the customer.',
  '2. Schedule the Alpha cut-over rehearsal.',
  '3. Add alerting to the Gamma ingestion pipeline.'
].join('\n');

/**
 * Scripted assistant turns for the management playground. The mock API from
 * `@newheap/platform-ai-chat/testing` plays them as contract events, so the panel runs
 * without the assistant back-end.
 */
export const ASSISTANT_PLAYGROUND_SCENARIO: NhAssistantMockScenario = {
  agents: [
    {
      id: PROJECT_ASSISTANT_ID,
      version: 1,
      displayNameKey: 'nh-assistant.agents.sample-project-assistant.name',
      descriptionKey: 'nh-assistant.agents.sample-project-assistant.description',
      canMutate: true
    },
    {
      id: CATALOG_GUIDE_ID,
      version: 1,
      displayNameKey: 'nh-assistant.agents.sample-catalog-guide.name',
      descriptionKey: 'nh-assistant.agents.sample-catalog-guide.description',
      canMutate: false
    }
  ],
  limits: { maxMessageChars: 2_000, maxToolCallsPerTurn: 8 },
  admin: {
    context: [
      'Sample Project Management tracks projects for divisions.',
      '',
      '- A project has a key such as PRJ-ALPHA, a name, a status and an optional deadline.',
      '- Statuses: Draft, Active, On hold, Completed and Archived.',
      '- Users see projects through application, division and project permissions.'
    ].join('\n'),
    policies: ['app.project.view', 'app.project.manage'],
    forwardUserTokenHosts: ['planning.sample.localhost'],
    tools: [
      { id: 'sample-api.project.list', source: 'bridge', effect: 'read-only', description: 'Lists projects with paging and filters.' },
      { id: 'sample-api.project.get-by-key', source: 'bridge', effect: 'read-only', description: 'Reads one project by key.' },
      { id: 'sample-api.project.update-status', source: 'bridge', effect: 'mutation', description: 'Changes the status of a project.' },
      { id: 'projects.portfolio-report', source: 'local', effect: 'read-only', description: 'Summarizes the project portfolio.' }
    ],
    agents: [
      {
        source: 'code',
        id: PROJECT_ASSISTANT_ID,
        displayName: 'Project assistant',
        description: 'Answers questions about projects and proposes status changes.',
        instructions: 'Help users with their projects. Propose status changes; never apply them without approval.',
        toolSelectors: ['sample-api.project.*', 'projects.*'],
        mcpServerIds: [],
        requiredPolicy: 'app.project.view',
        autonomy: 'execute',
        isEnabled: true
      },
      {
        source: 'code',
        id: CATALOG_GUIDE_ID,
        displayName: 'Catalog guide',
        description: 'Explains the sample catalog.',
        instructions: 'Explain the NewHeap sample catalog. Do not change data.',
        toolSelectors: [],
        mcpServerIds: [],
        requiredPolicy: null,
        autonomy: 'explain',
        isEnabled: true
      }
    ],
    mcpServers: [
      {
        id: PLAYGROUND_MCP_SERVER_ID,
        displayName: 'Project documents',
        url: 'https://documents.sample.localhost/mcp',
        authMode: 'api-key',
        headerName: 'X-Api-Key',
        secret: 'sample-only-key',
        requiredPolicy: 'app.project.view',
        isEnabled: true,
        remoteTools: PLAYGROUND_REMOTE_TOOLS
      }
    ]
  },
  timing: { firstEventDelayMs: 350, eventDelayMs: 45 },
  turns: [
    {
      match: /\b(hold|pause|status of)\b/i,
      steps: [
        { text: 'I will look up the project first.' },
        {
          tool: {
            toolId: 'sample-api.project.get-by-key',
            displayName: 'Get project',
            argumentsPreview: '{"key":"PRJ-ALPHA"}',
            resultPreview: '{"key":"PRJ-ALPHA","name":"Alpha migration","status":"active"}'
          }
        },
        { text: 'Alpha migration is active. Changing its status needs your approval.' },
        {
          approval: {
            toolId: 'sample-api.project.update-status',
            displayName: 'Update project status',
            summary: 'Set project Alpha migration to On hold',
            argumentsPreview: '{"key":"PRJ-ALPHA","status":"on-hold"}',
            targets: ['PRJ-ALPHA · Alpha migration'],
            expiresInSeconds: 300,
            resultPreview: '{"key":"PRJ-ALPHA","status":"on-hold"}',
            approved: [{ text: 'Done. **Alpha migration** is now *On hold*.' }],
            rejected: [{ text: 'Understood. I left Alpha migration unchanged.' }]
          }
        }
      ]
    },
    {
      match: /\bdelete\b/i,
      steps: [
        {
          tool: {
            toolId: 'sample-api.project.delete',
            displayName: 'Delete projects',
            argumentsPreview: '{"status":"archived"}',
            status: 'failed',
            resultCode: 'api-bridge-forbidden'
          }
        },
        { text: 'I cannot delete projects: your account does not have the permission for that action.' }
      ]
    },
    {
      match: /\b(report|long)\b/i,
      steps: [{ text: longReport }]
    },
    {
      match: /\b(html|script)\b/i,
      steps: [
        {
          text: 'Model output is Markdown only. Inline HTML stays visible as text: ' +
            '<script>alert("unsafe")</script> <img src="x" onerror="alert(1)"> ' +
            'and [unsafe links](javascript:alert(1)) lose their target, while [safe links](https://branding.newheap.com/) open in a new tab.'
        }
      ]
    },
    {
      match: /\b(unavailable|error|fail)\b/i,
      steps: [
        { text: 'Let me check that.' },
        { error: { code: 'assistant-model-unavailable', messageKey: 'nh-assistant.errors.assistant-model-unavailable' } }
      ]
    },
    {
      steps: [
        {
          tool: {
            toolId: 'sample-api.project.list',
            displayName: 'List projects',
            argumentsPreview: '{"filter":[{"key":"status","operator":"eq","value":"active"}],"page":1,"itemsPerPage":20}',
            resultPreview: '{"items":[{"key":"PRJ-ALPHA","status":"active"},{"key":"PRJ-GAMMA","status":"active"}],"total":2}'
          }
        },
        {
          text: 'Two projects are active:\n\n| Key | Project | Status |\n| --- | --- | --- |\n' +
            '| PRJ-ALPHA | Alpha migration | Active |\n| PRJ-GAMMA | Gamma analytics | Active |'
        }
      ]
    }
  ]
};
