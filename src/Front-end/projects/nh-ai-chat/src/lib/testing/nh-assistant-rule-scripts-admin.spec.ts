import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi, NhAssistantMockBackend } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { RuleScript, RuleScriptVersion } from '../models/assistant-rule-script.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { openAdminAgent as openAgent, settleAdmin as settle } from './nh-assistant-admin-navigation';

/**
 * RB-1 to RB-6 in the administration: the rule script of an agent and of each of its skills on the agent's
 * Build tab. The section shows only for an agent the code's ceiling allows. The editor lists what the script
 * sees, indents with Tab, checks the script with its line and column, opts in writes within the ceiling and
 * saves a version as the candidate, in shadow or, without four eyes, straight live. A dry run runs a version
 * on chosen subjects without effects; going live shows the latest dry run or says there is none, and four
 * eyes refuses the author. The mock API keeps the versions and refuses what the server refuses.
 */

/** A form flow of several requests; generous, so it only matters when the spec fails. */
const formFlowTimeout = 20_000;

const brokenScript = 'const documents = await tools.project_documents_read_v1({ projectId: subject.id });\nif (documents.data.documentCount === 0) {\n  done("none"));\n}';
const checkScript = 'const documents = await tools.project_documents_read_v1({ projectId: subject.id });\nif (documents.data.documentCount === 0) {\n  alerts.raise({ kind: "project-missing-document", key: "documents" });\n  await tools.projects_request_review_v1({ projectId: subject.id });\n}\ndone("checked");';

function scenario(options: { canAdminister?: boolean; requireFourEyes?: boolean } = {}): NhAssistantMockScenario {
  return {
    agents: [{ id: 'chat-helper', version: 1, displayNameKey: 'Chat helper', descriptionKey: 'Helps in the chat.', canMutate: false }],
    turns: [],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project', nl: 'Project' },
      searchable: false,
      subjects: [
        { id: 'PRJ-ALPHA', displayName: 'Alpha migration' },
        { id: 'PRJ-BETA', displayName: 'Beta onboarding' }
      ]
    }],
    admin: {
      canAdminister: options.canAdminister ?? true,
      tools: [
        { id: 'project-documents.read', source: 'local', effect: 'read-only', description: 'Reads the documents of a project.' },
        { id: 'projects.request-review', source: 'local', effect: 'mutation', description: 'Asks the review board.' }
      ],
      agents: [
        {
          source: 'code', id: 'document-check', displayName: 'Document check', description: 'Checks documents before a deadline.', instructions: 'Check.',
          toolSelectors: ['project-documents.read', 'projects.request-review'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false,
          skills: [{ skillId: 'follow-up', description: 'Follows up on a review.' }]
        },
        {
          source: 'code', id: 'planner', displayName: 'Project planner', description: 'Plans projects.', instructions: 'Plan.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] },
      ruleScripts: {
        requireFourEyes: options.requireFourEyes ?? true,
        agents: {
          'document-check': {
            writes: ['projects.request-review'],
            maxWritesPerRun: 1,
            tools: [
              { name: 'project_documents_read_v1', toolId: 'project-documents.read', signature: 'project_documents_read_v1(input: { projectId: string }): Promise<{ success: boolean; data?: { documentCount: number } }>;' },
              { name: 'projects_request_review_v1', toolId: 'projects.request-review', write: true }
            ],
            alertKinds: ['project-missing-document'],
            codeRule: 'DocumentCheckRule',
            versions: [
              { script: 'done("v1");', status: 'live', authorActorId: 'colleague', reason: 'First rules', createdAt: '2026-11-01T08:00:00Z' },
              {
                script: checkScript,
                writes: ['projects.request-review'],
                status: 'candidate',
                shadow: true,
                authorActorId: 'colleague',
                authorAgentId: 'planner',
                reason: 'Warn about missing documents',
                createdAt: '2026-11-02T08:00:00Z',
                shadowRunsOn: [{ type: 'project', id: 'PRJ-ALPHA' }]
              }
            ]
          }
        }
      }
    }
  };
}

async function configure(options: { canAdminister?: boolean; requireFourEyes?: boolean } = {}, language = 'en'): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario(options))
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use(language));
}

async function render<T>(component: Type<T>): Promise<{ fixture: ComponentFixture<T>; element: HTMLElement }> {
  const fixture = TestBed.createComponent(component);
  await settle(fixture);
  return { fixture, element: fixture.nativeElement as HTMLElement };
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const content = await response.text();
  return { status: response.status, body: content ? JSON.parse(content) : null };
}

async function click(fixture: ComponentFixture<unknown>, scope: ParentNode, selector: string): Promise<void> {
  const button = scope.querySelector<HTMLButtonElement>(selector);
  if (!button) {
    throw new Error(`"${selector}" not found.`);
  }
  button.click();
  await settle(fixture);
}

async function type(fixture: ComponentFixture<unknown>, field: HTMLInputElement | HTMLTextAreaElement, value: string, change = false): Promise<void> {
  field.value = value;
  field.dispatchEvent(new Event('input'));
  if (change) {
    field.dispatchEvent(new Event('change'));
  }
  await settle(fixture);
}

function version(section: HTMLElement, number: number): HTMLElement {
  const row = section.querySelector<HTMLElement>(`[data-rule-script-version="${number}"]`);
  if (!row) {
    throw new Error(`Version ${number} not found.`);
  }
  return row;
}

async function openBuild(fixture: ComponentFixture<unknown>, element: HTMLElement, agentId = 'document-check'): Promise<HTMLElement> {
  const page = await openAgent(fixture, element, agentId, 'build');
  return page;
}

/** Chooses projects for a dry run by id, as the picker of a type without search takes them. */
async function chooseSubjects(fixture: ComponentFixture<unknown>, panel: HTMLElement, ids: string[]): Promise<void> {
  for (const id of ids) {
    const field = panel.querySelector<HTMLInputElement>('[data-rule-script-dry-run-subject] input')!;
    await type(fixture, field, id, true);
    await click(fixture, panel, '[data-rule-script-dry-run-add]');
  }
}

describe('Rule scripts in the administration', () => {
  it('shows the section only for an agent the code\'s ceiling allows, for its own rules and each skill', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    const planner = await openBuild(fixture, element, 'planner');
    expect(planner.querySelector('[data-rule-scripts]')).toBeNull();
    (planner.querySelector('[data-back]') as HTMLButtonElement).click();
    await settle(fixture);

    const page = await openBuild(fixture, element);
    const section = page.querySelector('[data-rule-scripts]') as HTMLElement;
    expect(text(section.querySelector('h4'))).toBe('Rule script');
    expect(text(section.querySelector('[data-rule-script-in-effect]'))).toContain('Version 1');
    expect(text(section.querySelector('[data-rule-script-in-effect]'))).toContain('DocumentCheckRule');
    expect(text(section.querySelector('[data-rule-script-candidate]'))).toContain('runs in shadow');
    expect(text(section.querySelector('[data-rule-script-ceiling]'))).toContain('projects.request-review');
    expect(section.querySelector('[data-rule-script-four-eyes]')).not.toBeNull();

    // The candidate names the agent that wrote it for the person, with the reason.
    const candidate = version(section, 2);
    expect(candidate.getAttribute('data-rule-script-status')).toBe('candidate');
    expect(candidate.querySelector('[data-rule-script-version-shadow]')).not.toBeNull();
    expect(text(candidate.querySelector('[data-rule-script-version-author]'))).toContain('Project planner');
    expect(text(candidate.querySelector('[data-rule-script-version-reason]'))).toContain('Warn about missing documents');
    expect(version(section, 1).querySelector('[data-rule-script-action="live"]')).toBeNull();

    // A skill has a rule script of its own.
    await click(fixture, section, '[data-rule-script-scope="follow-up"]');
    expect(section.querySelector('[data-rule-script-scope="follow-up"]')?.getAttribute('aria-pressed')).toBe('true');
    expect(section.querySelector('[data-rule-script-no-versions]')).not.toBeNull();
    expect(TestBed.inject(NhAssistantMockBackend).requests.some(request => request.path === 'admin/agents/document-check/skills/follow-up/rule-script')).toBeTrue();
  });

  it('writes a version: what the script sees, Tab, the check with its line and column, the writes and the save as candidate', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const section = (await openBuild(fixture, element)).querySelector('[data-rule-scripts]') as HTMLElement;

    await click(fixture, section, '[data-rule-script-new]');
    const editor = section.querySelector('[data-rule-script-editor]') as HTMLElement;
    const textarea = editor.querySelector('[data-rule-script-text]') as HTMLTextAreaElement;
    // A new version starts from the live one.
    expect(textarea.value).toBe('done("v1");');

    // The catalog: the read with its signature, the write not yet chosen, the alert kind and the platform.
    const catalog = editor.querySelector('[data-rule-script-catalog]') as HTMLElement;
    expect(text(catalog.querySelector('[data-rule-script-tool="project_documents_read_v1"]'))).toContain('documentCount');
    expect(catalog.querySelector('[data-rule-script-tool="projects_request_review_v1"]')?.getAttribute('data-rule-script-tool-callable')).toBe('false');
    expect(catalog.querySelector('[data-rule-script-alert-kind="project-missing-document"]')).not.toBeNull();
    expect(text(catalog.querySelector('[data-rule-script-platform]'))).toContain('declare function done');

    // Tab indents at the caret; after Escape, Tab leaves the field.
    textarea.setSelectionRange(0, 0);
    const tab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
    textarea.dispatchEvent(tab);
    await settle(fixture);
    expect(tab.defaultPrevented).toBeTrue();
    expect(textarea.value).toBe('  done("v1");');
    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle(fixture);
    expect(editor.querySelector('[data-rule-script-tab-leaves]')).not.toBeNull();
    const leave = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
    textarea.dispatchEvent(leave);
    expect(leave.defaultPrevented).toBeFalse();

    // The check names where the script does not parse and moves the caret there.
    await type(fixture, textarea, brokenScript);
    await click(fixture, editor, '[data-rule-script-check]');
    const invalid = editor.querySelector('[data-rule-script-check-result="invalid"]') as HTMLElement;
    expect(text(invalid)).toContain('The script does not parse.');
    expect(text(invalid.querySelector('[data-rule-script-go-to]'))).toBe('Go to line 3, column 15');
    await click(fixture, invalid, '[data-rule-script-go-to]');
    expect(text(editor.querySelector('[data-rule-script-caret]'))).toBe('Line 3, column 15');

    await type(fixture, textarea, checkScript);
    expect(editor.querySelector('[data-rule-script-check-result]')).toBeNull();
    await click(fixture, editor, '[data-rule-script-check]');
    expect(editor.querySelector('[data-rule-script-check-result="valid"]')).not.toBeNull();

    // Inserting a call puts it at the caret.
    textarea.setSelectionRange(textarea.value.length, textarea.value.length);
    await click(fixture, catalog, '[data-rule-script-tool="project_documents_read_v1"] [data-rule-script-insert-tool]');
    expect(textarea.value.endsWith('const result = await tools.project_documents_read_v1({ });\n')).toBeTrue();
    await type(fixture, textarea, checkScript);

    // The write is chosen within the ceiling, which the catalog reads again.
    const write = editor.querySelector('[data-rule-script-write-choice="projects.request-review"] input') as HTMLInputElement;
    write.click();
    await settle(fixture);
    expect(catalog.querySelector('[data-rule-script-tool="projects_request_review_v1"]')?.getAttribute('data-rule-script-tool-callable')).toBe('true');

    // Four eyes: nobody saves straight live.
    expect((editor.querySelector('[data-rule-script-save-option="live"] input') as HTMLInputElement).disabled).toBeTrue();
    expect(editor.querySelector('[data-rule-script-four-eyes-hint]')).not.toBeNull();
    (editor.querySelector('[data-rule-script-save-option="shadow"] input') as HTMLInputElement).click();
    await settle(fixture);
    expect(text(editor.querySelector('[data-rule-script-save-explain]'))).toContain('Candidate version 2 is discarded.');

    await type(fixture, editor.querySelector('[data-rule-script-reason]') as HTMLInputElement, 'Ask for a review');
    await click(fixture, editor, '[data-rule-script-save]');

    expect(section.querySelector('[data-rule-script-editor]')).toBeNull();
    expect(text(section.querySelector('[data-rule-script-notice]'))).toBe('Version 3 was saved as the candidate and runs in shadow.');
    expect(version(section, 3).getAttribute('data-rule-script-status')).toBe('candidate');
    expect(version(section, 2).getAttribute('data-rule-script-status')).toBe('discarded');
    const saved = (await call('GET', 'admin/agents/document-check/rule-script')).body as RuleScript;
    expect(saved.versions[0].writes).toEqual(['projects.request-review']);
    expect(saved.versions[0].reason).toBe('Ask for a review');
    expect(saved.candidateShadow).toBeTrue();
  }, formFlowTimeout);

  it('dry-runs a version on chosen subjects and shows per subject what it would do', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const section = (await openBuild(fixture, element)).querySelector('[data-rule-scripts]') as HTMLElement;

    await click(fixture, version(section, 2), '[data-rule-script-action="dry-run"]');
    const panel = version(section, 2).querySelector('[data-rule-script-panel="dry-run"]') as HTMLElement;
    expect(panel.querySelector('[data-rule-script-dry-run-never]')).not.toBeNull();
    expect((panel.querySelector('[data-rule-script-dry-run-start]') as HTMLButtonElement).disabled).toBeTrue();

    await chooseSubjects(fixture, panel, ['PRJ-ALPHA', 'PRJ-BETA']);
    expect(Array.from(panel.querySelectorAll('[data-rule-script-dry-run-chosen-subject]')).map(item => item.getAttribute('data-rule-script-dry-run-chosen-subject')))
      .toEqual(['project/PRJ-ALPHA', 'project/PRJ-BETA']);
    await click(fixture, panel, '[data-rule-script-dry-run-chosen-subject="project/PRJ-BETA"] button');
    await click(fixture, panel, '[data-rule-script-dry-run-start]');

    const subject = panel.querySelector('[data-rule-script-subject="project/PRJ-ALPHA"]') as HTMLElement;
    expect(subject.getAttribute('data-rule-script-subject-status')).toBe('completed');
    expect(text(subject.querySelector('summary'))).toContain('Alpha migration');
    expect(Array.from(subject.querySelectorAll('[data-rule-script-call]')).map(item => item.getAttribute('data-rule-script-call')))
      .toEqual(['project_documents_read_v1', 'projects_request_review_v1']);
    expect(text(subject.querySelector('[data-rule-script-call="projects_request_review_v1"]'))).toContain('Recorded, not run');
    expect(subject.querySelector('[data-rule-script-write="projects_request_review_v1"]')?.getAttribute('data-rule-script-write-outcome')).toBe('would-propose');
    expect(text(subject.querySelector('[data-rule-script-write]'))).toContain('Would become a proposal');
    expect(subject.querySelector('[data-rule-script-alert="project-missing-document"]')).not.toBeNull();
    expect(text(subject.querySelector('[data-rule-script-subject-end]'))).toContain('Done');
    expect(text(panel.querySelector('[data-rule-script-record-summary]'))).toContain('1 of 1 completed');
  }, formFlowTimeout);

  it('stops a dry run at a write the version did not opt in to, with its line and column', async () => {
    await configure({ requireFourEyes: false });
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const section = (await openBuild(fixture, element)).querySelector('[data-rule-scripts]') as HTMLElement;
    const saved = await call('POST', 'admin/agents/document-check/rule-script/versions', { script: checkScript, writes: [], expectedRevision: 2 });
    expect(saved.status).toBe(200);
    await click(fixture, section, '[data-rule-script-scope="follow-up"]');
    await click(fixture, section, '[data-rule-script-scope=""]');

    await click(fixture, version(section, 3), '[data-rule-script-action="dry-run"]');
    const panel = version(section, 3).querySelector('[data-rule-script-panel="dry-run"]') as HTMLElement;
    await chooseSubjects(fixture, panel, ['PRJ-ALPHA']);
    await click(fixture, panel, '[data-rule-script-dry-run-start]');

    const subject = panel.querySelector('[data-rule-script-subject="project/PRJ-ALPHA"]') as HTMLElement;
    expect(subject.getAttribute('data-rule-script-subject-status')).toBe('stopped');
    expect(text(subject.querySelector('[data-rule-script-subject-code]'))).toContain('The script called a write this version did not opt in to.');
    expect(text(subject.querySelector('[data-rule-script-subject-code]'))).toContain('Line 4, column 9');
  }, formFlowTimeout);

  it('shows the latest dry run before going live, refuses the author under four eyes and sets another person\'s version live', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const section = (await openBuild(fixture, element)).querySelector('[data-rule-scripts]') as HTMLElement;

    // Without a dry run the confirmation says there is none.
    await click(fixture, version(section, 2), '[data-rule-script-action="live"]');
    let confirm = version(section, 2).querySelector('[data-rule-script-confirm-form="live"]') as HTMLElement;
    expect(confirm.querySelector('[data-rule-script-live-no-dry-run]')).not.toBeNull();
    expect(text(confirm.querySelector('[data-rule-script-live-explain]'))).toContain('Version 2 replaces live version 1.');
    expect(confirm.querySelector('[data-rule-script-live-four-eyes]')).not.toBeNull();
    await click(fixture, confirm, '.card-actions button.secondary');

    // With one it shows it.
    await call('POST', 'admin/agents/document-check/rule-script/versions/2/dry-run', { subjects: [{ type: 'project', id: 'PRJ-ALPHA' }] });
    await click(fixture, version(section, 2), '[data-rule-script-action="live"]');
    confirm = version(section, 2).querySelector('[data-rule-script-confirm-form="live"]') as HTMLElement;
    expect(confirm.querySelector('[data-rule-script-live-no-dry-run]')).toBeNull();
    expect(confirm.querySelector('[data-rule-script-live-dry-run] [data-rule-script-subject="project/PRJ-ALPHA"]')).not.toBeNull();

    await click(fixture, confirm, '[data-rule-script-confirm="live"]');
    expect(text(section.querySelector('[data-rule-script-notice]'))).toBe('Version 2 is live. The next rule turn runs it.');
    expect(version(section, 2).getAttribute('data-rule-script-status')).toBe('live');
    expect(version(section, 1).getAttribute('data-rule-script-status')).toBe('superseded');
    expect(text(section.querySelector('[data-rule-script-in-effect]'))).toContain('Version 2');

    // A version the administrator saved is refused live by four eyes, with a clear text.
    await call('POST', 'admin/agents/document-check/rule-script/versions', { script: checkScript, writes: [], expectedRevision: 3 });
    await click(fixture, section, '[data-rule-script-scope="follow-up"]');
    await click(fixture, section, '[data-rule-script-scope=""]');
    await click(fixture, version(section, 3), '[data-rule-script-action="live"]');
    confirm = version(section, 3).querySelector('[data-rule-script-confirm-form="live"]') as HTMLElement;
    await click(fixture, confirm, '[data-rule-script-confirm="live"]');
    expect(text(confirm.querySelector('[data-rule-script-error]'))).toContain('Ask another administrator to set it live.');
    expect(version(section, 3).getAttribute('data-rule-script-status')).toBe('candidate');
  }, formFlowTimeout);

  it('switches shadow, lists the shadow runs, compares two versions and discards the candidate', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const section = (await openBuild(fixture, element)).querySelector('[data-rule-scripts]') as HTMLElement;

    await click(fixture, version(section, 2), '[data-rule-script-action="shadow-runs"]');
    const runs = version(section, 2).querySelector('[data-rule-script-shadow-runs]') as HTMLElement;
    expect(runs.querySelectorAll('[data-rule-script-record-kind="shadow"]').length).toBe(1);
    expect(runs.querySelector('[data-rule-script-record-run]')).not.toBeNull();

    await click(fixture, version(section, 2), '[data-rule-script-action="shadow-off"]');
    expect(text(section.querySelector('[data-rule-script-notice]'))).toBe('Version 2 no longer runs in shadow.');
    expect(version(section, 2).querySelector('[data-rule-script-version-shadow]')).toBeNull();
    expect(version(section, 2).querySelector('[data-rule-script-action="shadow-on"]')).not.toBeNull();

    await click(fixture, version(section, 2), '[data-rule-script-action="compare"]');
    const comparison = version(section, 2).querySelector('[data-rule-script-comparison]') as HTMLElement;
    expect(text(comparison.querySelector('[data-rule-script-compare-summary]'))).toContain('Version 1 to version 2');
    expect(comparison.querySelectorAll('[data-rule-script-diff-line="added"]').length).toBe(6);
    expect(comparison.querySelectorAll('[data-rule-script-diff-line="removed"]').length).toBe(1);
    expect(text(comparison.querySelector('[data-rule-script-writes-added]'))).toBe('projects.request-review');

    await click(fixture, version(section, 2), '[data-rule-script-action="discard"]');
    const discard = version(section, 2).querySelector('[data-rule-script-confirm-form="discard"]') as HTMLElement;
    await type(fixture, discard.querySelector('input') as HTMLInputElement, 'Not needed');
    await click(fixture, discard, '[data-rule-script-confirm="discard"]');
    expect(version(section, 2).getAttribute('data-rule-script-status')).toBe('discarded');
    expect(text(section.querySelector('[data-rule-script-candidate]'))).toBe('None');
  }, formFlowTimeout);

  it('asks to read the rule script again after someone else changed it, and keeps the text', async () => {
    await configure({ requireFourEyes: false });
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const section = (await openBuild(fixture, element)).querySelector('[data-rule-scripts]') as HTMLElement;

    await click(fixture, section, '[data-rule-script-new]');
    const editor = section.querySelector('[data-rule-script-editor]') as HTMLElement;
    await type(fixture, editor.querySelector('[data-rule-script-text]') as HTMLTextAreaElement, 'done("mine");');
    (editor.querySelector('[data-rule-script-save-option="live"] input') as HTMLInputElement).click();
    await settle(fixture);
    expect(text(editor.querySelector('[data-rule-script-save-explain]'))).toContain('replaces live version 1');
    // Someone else saves a version in the meantime.
    await call('POST', 'admin/agents/document-check/rule-script/versions', { script: 'done("theirs");', writes: [], expectedRevision: 2 });

    await click(fixture, editor, '[data-rule-script-save]');
    expect(text(editor.querySelector('[data-rule-script-save-error]'))).toContain('Someone else changed this');
    await click(fixture, editor, '[data-rule-script-reload]');
    await click(fixture, editor, '[data-rule-script-save]');

    expect(text(section.querySelector('[data-rule-script-notice]'))).toBe('Version 4 was saved and is live.');
    expect(((await call('GET', 'admin/agents/document-check/rule-script')).body as RuleScript).liveVersion).toBe(4);
  }, formFlowTimeout);

  it('reads in Dutch', async () => {
    await configure({}, 'nl');
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const section = (await openBuild(fixture, element)).querySelector('[data-rule-scripts]') as HTMLElement;

    expect(text(section.querySelector('h4'))).toBe('Regelscript');
    expect(text(version(section, 2).querySelector('[data-rule-script-action="dry-run"]'))).toBe('Proefrun');
    expect(text(version(section, 2).querySelector('[data-rule-script-action="live"]'))).toBe('Live zetten');
  });

  it('is refused by the mock API as by the server', async () => {
    await configure();
    const base = 'admin/agents/document-check/rule-script';

    const outside = await call('GET', 'admin/agents/planner/rule-script');
    const outsideCatalog = await call('GET', 'admin/agents/planner/rule-script/catalog');
    const unknownSkill = await call('GET', 'admin/agents/document-check/skills/unknown/rule-script');
    const notAllowedWrite = await call('POST', `${base}/versions`, { script: 'done("x");', writes: ['projects.delete'], expectedRevision: 2 });
    const fourEyes = await call('POST', `${base}/versions`, { script: 'done("x");', writes: [], expectedRevision: 2, live: true });
    const invalid = await call('POST', `${base}/versions`, { script: 'done(', writes: [], expectedRevision: 2 });
    const stale = await call('POST', `${base}/versions`, { script: 'done("x");', writes: [], expectedRevision: 1 });
    const noSubjects = await call('POST', `${base}/versions/2/dry-run`, { subjects: [] });
    const tooMany = await call('POST', `${base}/versions/2/dry-run`, { subjects: Array.from({ length: 21 }, (_, index) => ({ type: 'project', id: `P-${index}` })) });
    const unknownVersion = await call('POST', `${base}/versions/9/dry-run`, { subjects: [{ type: 'project', id: 'PRJ-ALPHA' }] });
    const notCandidate = await call('POST', `${base}/versions/1/discard`, { expectedRevision: 2 });
    const noDryRun = await call('GET', `${base}/versions/2/dry-run`);
    const check = await call('POST', `${base}/check`, { script: 'if (x) {' });

    expect(outside.body.allowed).toBeFalse();
    expect([outsideCatalog.status, outsideCatalog.body.code]).toEqual([409, 'assistant-rule-scripts-not-allowed']);
    expect([unknownSkill.status, unknownSkill.body.code]).toEqual([404, 'assistant-skill-not-found']);
    expect([notAllowedWrite.status, notAllowedWrite.body.code, notAllowedWrite.body.errors]).toEqual([400, 'assistant-rule-script-write-not-allowed', { writes: ['not-allowed'] }]);
    expect([fourEyes.status, fourEyes.body.code]).toEqual([409, 'assistant-rule-script-four-eyes']);
    expect([invalid.status, invalid.body.errors]).toEqual([400, { script: ['invalid'] }]);
    expect([stale.status, stale.body.code]).toEqual([409, 'assistant-version-conflict']);
    expect([noSubjects.status, noSubjects.body.code]).toEqual([400, 'assistant-rule-script-subjects-invalid']);
    expect([tooMany.status, tooMany.body.code]).toEqual([400, 'assistant-rule-script-subjects-invalid']);
    expect([unknownVersion.status, unknownVersion.body.code]).toEqual([404, 'assistant-rule-script-version-not-found']);
    expect([notCandidate.status, notCandidate.body.code]).toEqual([409, 'assistant-rule-script-not-candidate']);
    expect(noDryRun.status).toBe(204);
    expect([check.body.valid, check.body.code, check.body.line, check.body.column]).toEqual([false, 'ai-script-syntax-error', 1, 9]);

    const live = await call('POST', `${base}/versions/2/live`, { expectedRevision: 2, reason: 'Checked' });
    expect(live.status).toBe(200);
    expect((live.body.version as RuleScriptVersion).status).toBe('live');
    expect(live.body.latestDryRun).toBeNull();
  });

  it('answers 403 to someone outside the administration', async () => {
    await configure({ canAdminister: false });

    const read = await call('GET', 'admin/agents/document-check/rule-script');
    const save = await call('POST', 'admin/agents/document-check/rule-script/versions', { script: 'done("x");', writes: [], expectedRevision: 2 });

    expect([read.status, read.body.code]).toEqual([403, 'assistant-forbidden']);
    expect([save.status, save.body.code]).toEqual([403, 'assistant-forbidden']);
  });
});
