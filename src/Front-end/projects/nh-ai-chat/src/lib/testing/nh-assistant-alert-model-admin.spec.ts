import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { AgentAlertModel, AlertModelSwitch, AlertModelVersion } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { openAdminAgent as openAgent, settleAdmin as settle } from './nh-assistant-admin-navigation';

/**
 * FB87-9: the alert kinds an agent's model may raise, on the agent's Rollout. A kind the agent lists whose
 * code lets a model raise it lists with when it applies, what the model fills and its switch; it goes to
 * shadow first, where the model's alerts reach nobody, and only from shadow to people, with an
 * explanation and a note, and back to shadow or off at any time. An agent without such a kind shows no
 * section. The mock API keeps the versions and refuses what the server refuses.
 */

function scenario(canAdminister = true): NhAssistantMockScenario {
  return {
    agents: [{ id: 'chat-helper', version: 1, displayNameKey: 'Chat helper', descriptionKey: 'Helps in the chat.', canMutate: false }],
    turns: [],
    subjectTypes: [{ id: 'project', names: { en: 'Project', nl: 'Project' }, subjects: [] }],
    admin: {
      canAdminister,
      tools: [{ id: 'projects.search', source: 'local', effect: 'read-only', description: 'Search projects.' }],
      agents: [
        {
          source: 'code', id: 'reviewer', displayName: 'Deadline reviewer', description: 'Reviews deadlines.', instructions: 'Review.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'observe', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false
        },
        {
          source: 'code', id: 'planner', displayName: 'Project planner', description: 'Plans projects.', instructions: 'Plan.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false
        },
        {
          source: 'code', id: 'chat-helper', displayName: 'Chat helper', description: 'Helps in the chat.', instructions: 'Help.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'observe', isEnabled: true,
          chatOnly: true
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] },
      alertModel: {
        reviewer: [{
          kindId: 'document-deadline',
          description: 'A document of the project is due within a month and its work is not ready.',
          parameters: [
            { name: 'deadline', type: 'date', description: 'The deadline.', choices: [], minimum: null, maximum: null, maxLength: null, key: true },
            { name: 'document', type: 'text', description: 'The document.', choices: [], minimum: null, maximum: null, maxLength: 80, key: true },
            { name: 'daysLeft', type: 'whole-number', description: 'Days left.', choices: [], minimum: 0, maximum: 30, maxLength: null, key: false }
          ]
        }],
        'chat-helper': [{ kindId: 'document-deadline', description: 'A document is due.', parameters: [] }]
      }
    }
  };
}

async function configure(canAdminister = true, language = 'en'): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario(canAdminister))
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use(language));
  TestBed.inject(TranslateService).setTranslation(
    language,
    { 'nh-assistant': { 'alert-kinds': { 'document-deadline': { name: language === 'nl' ? 'Deadline van een document' : 'Document deadline' } } } },
    true);
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

function actions(row: HTMLElement): (string | null)[] {
  return Array.from(row.querySelectorAll('[data-alert-model-action]')).map(item => item.getAttribute('data-alert-model-action'));
}

async function choose(fixture: ComponentFixture<unknown>, row: HTMLElement, mode: string, note?: string): Promise<void> {
  (row.querySelector(`[data-alert-model-action="${mode}"]`) as HTMLButtonElement).click();
  await settle(fixture);
  if (note !== undefined) {
    const input = row.querySelector('[data-alert-model-form] input') as HTMLInputElement;
    input.value = note;
    input.dispatchEvent(new Event('input'));
  }
  (row.querySelector('[data-alert-model-confirm]') as HTMLButtonElement).click();
  await settle(fixture);
}

describe('Alerts from an agent\'s model in the administration', () => {
  it('lists the kinds on the rollout and goes to shadow first, then to people, then off, explaining each step', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openAgent(fixture, element, 'reviewer', 'rollout');
    const section = page.querySelector('[data-alert-model]') as HTMLElement;
    const row = () => section.querySelector('[data-alert-model-kind="document-deadline"]') as HTMLElement;

    // The kind lists by its name with what the model fills, off, with shadow as its only way on.
    expect(text(row().querySelector('strong'))).toBe('Document deadline');
    expect(text(row())).toContain('A document of the project is due within a month');
    expect(Array.from(row().querySelectorAll('[data-alert-model-parameter]')).map(item => item.getAttribute('data-alert-model-parameter')))
      .toEqual(['deadline', 'document', 'daysLeft']);
    expect(text(row().querySelector('[data-alert-model-parameter="document"]'))).toBe('document · a short text · key');
    expect(row().getAttribute('data-alert-model-mode')).toBe('off');
    expect(text(row().querySelector('[data-alert-model-status]'))).toBe('Off');
    expect(actions(row())).toEqual(['shadow']);

    // Shadow explains that the model's alerts reach nobody before it applies.
    (row().querySelector('[data-alert-model-action="shadow"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(row().querySelector('[data-alert-model-explain]'))).toContain('Document deadline');
    expect(text(row().querySelector('[data-alert-model-explain]'))).toContain('reach nobody');
    (row().querySelector('[data-alert-model-confirm]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(row().getAttribute('data-alert-model-mode')).toBe('shadow');
    expect(text(row().querySelector('[data-alert-model-changed]'))).toContain('Version 1');
    expect(actions(row())).toEqual(['live', 'off']);

    // Going to people says whom they reach, and keeps the note.
    (row().querySelector('[data-alert-model-action="live"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(row().querySelector('[data-alert-model-explain]'))).toContain('reach the people the kind chooses');
    (row().querySelector('.change-form button.secondary') as HTMLButtonElement).click();
    await settle(fixture);
    expect(row().querySelector('[data-alert-model-form]')).toBeNull();
    await choose(fixture, row(), 'live', 'Shadow alerts were right for two weeks');
    expect(row().getAttribute('data-alert-model-mode')).toBe('live');
    expect(text(row().querySelector('[data-alert-model-status]'))).toBe('Reaches people');
    expect(text(row())).toContain('Shadow alerts were right for two weeks');

    await choose(fixture, row(), 'off');
    expect(row().getAttribute('data-alert-model-mode')).toBe('off');

    const versions = (await call('GET', 'admin/agents/reviewer/alert-kinds/document-deadline/model/versions')).body as AlertModelVersion[];
    expect(versions.map(version => [version.version, version.mode])).toEqual([[3, 'off'], [2, 'live'], [1, 'shadow']]);
  });

  it('shows no section for an agent without such a kind, and no switch for an agent without work of its own', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const planner = await openAgent(fixture, element, 'planner', 'rollout');
    expect(planner.querySelector('[data-alert-model]')).toBeNull();
    expect(TestBed.inject(NhAssistantMockBackend).requests.filter(request => request.path === 'admin/agents/planner/alert-model').length).toBe(1);
  });

  it('shows why a refused switch did not apply', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openAgent(fixture, element, 'reviewer', 'rollout');
    // Someone else switched it in the meantime, so the version the page read is stale.
    await call('PUT', 'admin/agents/reviewer/alert-kinds/document-deadline/model', { mode: 'shadow', expectedVersion: 0 });
    const row = page.querySelector('[data-alert-model-kind="document-deadline"]') as HTMLElement;

    await choose(fixture, row, 'shadow');

    expect(row.querySelector('[data-alert-model-error]')).not.toBeNull();
    expect(text(row.querySelector('[data-alert-model-error]'))).not.toBe('');
  });

  it('reads in Dutch', async () => {
    await configure(true, 'nl');
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const page = await openAgent(fixture, element, 'reviewer', 'rollout');
    const section = page.querySelector('[data-alert-model]') as HTMLElement;

    expect(text(section.querySelector('h4'))).toBe('Attenties van het taalmodel');
    expect(text(section.querySelector('[data-alert-model-status]'))).toBe('Uit');
    expect(text(section.querySelector('[data-alert-model-parameter="deadline"]'))).toBe('deadline · een datum · sleutel');
  });

  it('is refused by the mock API as by the server: shadow first, a listed kind, work of its own and the administration policy', async () => {
    await configure();
    const route = 'admin/agents/reviewer/alert-kinds/document-deadline/model';

    const listed = (await call('GET', 'admin/agents/reviewer/alert-model')).body as AgentAlertModel;
    expect(listed.applies).toBeTrue();
    expect(listed.kinds.map(kind => [kind.kindId, kind.mode, kind.version, kind.kindEnabled])).toEqual([['document-deadline', 'off', 0, true]]);

    const liveFirst = await call('PUT', route, { mode: 'live', expectedVersion: 0 });
    const notListed = await call('PUT', 'admin/agents/planner/alert-kinds/document-deadline/model', { mode: 'shadow', expectedVersion: 0 });
    const chatOnly = await call('PUT', 'admin/agents/chat-helper/alert-kinds/document-deadline/model', { mode: 'shadow', expectedVersion: 0 });
    const invalid = await call('PUT', route, { mode: 'sometimes', expectedVersion: 0 });
    const shadow = await call('PUT', route, { mode: 'shadow', expectedVersion: 0 });
    const stale = await call('PUT', route, { mode: 'live', expectedVersion: 0 });

    expect([liveFirst.status, liveFirst.body.code]).toEqual([409, 'assistant-alert-model-shadow-first']);
    expect([notListed.status, notListed.body.code]).toEqual([409, 'assistant-alert-model-not-allowed']);
    expect([chatOnly.status, chatOnly.body.code]).toEqual([409, 'assistant-alert-model-not-allowed']);
    expect(invalid.status).toBe(400);
    expect((shadow.body as AlertModelSwitch).version).toBe(1);
    expect([stale.status, stale.body.code]).toEqual([409, 'assistant-version-conflict']);
  });

  it('answers 403 to someone outside the administration', async () => {
    await configure(false);

    const read = await call('GET', 'admin/agents/reviewer/alert-model');
    const write = await call('PUT', 'admin/agents/reviewer/alert-kinds/document-deadline/model', { mode: 'shadow', expectedVersion: 0 });

    expect([read.status, read.body.code]).toEqual([403, 'assistant-forbidden']);
    expect([write.status, write.body.code]).toEqual([403, 'assistant-forbidden']);
  });
});
