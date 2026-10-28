import { Component, Type, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, of } from 'rxjs';
import {
  NhAssistantAdminAgentsComponent,
  NhAssistantAdminMcpServersComponent,
  NhAssistantAgentRunHistoryComponent
} from '../../../admin/src/public-api';
import {
  NhAssistantObjectAgentsComponent,
  NhAssistantRunResultComponent,
  NhAssistantRunTimelineComponent
} from '../../../agent-runs/src/public-api';
import { AgentRunResult } from '../models/assistant-agent-run.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { backToAdminList, openAdminAgent } from './nh-assistant-admin-navigation';
import { NhAssistantApiError } from '../services/nh-assistant-transport';

/**
 * The views of agent runs and the administration show failures as their codes say: run now ends in a
 * warning while the agent is paused, in information while the start waits for a saved change and in
 * the refusal's text otherwise; a run's failure reads as its registered text; a download names why it
 * failed; the agents page says that the run catalog could not be read and reads it again; and the
 * server's field errors of an MCP server and a run filter show at their fields.
 */
const rolledBack = 'The run could not be queued. Please try again.';

function scenario(overrides: Partial<NhAssistantMockScenario> = {}): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'Project assistant', descriptionKey: 'Helps with projects.', canMutate: true }],
    turns: [],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project' },
      pluralNames: { en: 'Projects' },
      subjects: [{ id: 'p-1', displayName: 'PRJ-1 Pietje', role: 'approver' }, { id: 'p-2', displayName: 'PRJ-2 Puk' }]
    }],
    runs: [
      {
        id: 'run-rolled-back', agentId: 'projects', subjectType: 'project', subjectId: 'p-1', status: 'failed',
        failureCode: 'assistant-trigger-rolled-back', createdAt: '2026-10-18T10:00:00Z'
      },
      {
        id: 'run-unknown', agentId: 'projects', subjectType: 'project', subjectId: 'p-2', status: 'failed',
        failureCode: 'assistant-application-specific', createdAt: '2026-10-17T10:00:00Z'
      }
    ],
    admin: {
      agents: [{
        source: 'admin', id: 'projects', displayName: 'Project assistant', description: 'Helps with projects.', instructions: 'Help.',
        toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
        worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false
      }],
      mcpServers: [{
        id: 'files', displayName: 'Files', url: 'https://files.example.com/mcp', authMode: 'bearer', headerName: null,
        secret: 'secret', requiredPolicy: null, isEnabled: true
      }],
      runCatalog: { triggerEvents: [], subjectSets: [], approvalPolicies: [] }
    },
    ...overrides
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(script = scenario(), administers = true): Promise<NhAssistantMockBackend> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(script)
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  const backend = TestBed.inject(NhAssistantMockBackend);
  backend.setCanAdminister(administers);
  return backend;
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

async function render<T>(component: Type<T>, inputs: Record<string, unknown> = {}): Promise<{ fixture: ComponentFixture<T>; element: HTMLElement }> {
  const fixture = TestBed.createComponent(component);
  for (const [name, value] of Object.entries(inputs)) {
    fixture.componentRef.setInput(name, value);
  }
  return { fixture, element: await settle(fixture) };
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function button(scope: ParentNode, label: string): HTMLButtonElement {
  const match = Array.from(scope.querySelectorAll('button')).find(item => item.textContent?.trim() === label);
  if (!match) {
    throw new Error(`Button "${label}" not found.`);
  }
  return match;
}

@Component({
  standalone: true,
  imports: [NhAssistantObjectAgentsComponent],
  template: `<div class="page" style="width: 375px"><nh-assistant-object-agents [subjectType]="subjectType()" [subjectId]="subjectId()" /></div>`
})
class ObjectPageHost {
  readonly subjectType = signal('project');
  readonly subjectId = signal('p-2');
}

describe('Run now in the views of agent runs', () => {
  async function runNow(): Promise<{ fixture: ComponentFixture<ObjectPageHost>; dialog: HTMLDialogElement }> {
    const { fixture, element } = await render(ObjectPageHost);
    (element.querySelector('[data-object-run-now]') as HTMLButtonElement).click();
    await settle(fixture);
    const dialog = element.querySelector('nh-assistant-run-now-dialog dialog') as HTMLDialogElement;
    (dialog.querySelector('[data-run-now-submit]') as HTMLButtonElement).click();
    await settle(fixture);
    return { fixture, dialog };
  }

  it('ends in a warning while the agent is paused, never in a started run', async () => {
    await configure();
    expect((await call('POST', 'admin/agents/projects/state', { state: 'paused', reason: null })).status).toBe(200);

    const { dialog } = await runNow();

    const receipt = dialog.querySelector('[data-receipt] .message') as HTMLElement;
    expect(receipt.classList).toContain('warning');
    expect(receipt.classList).not.toContain('success');
    expect(receipt.getAttribute('data-outcome')).toBe('waiting');
    expect(text(receipt)).toBe('The agent is paused. This run starts when an administrator resumes it.');
    expect(document.activeElement).toBe(receipt);
  });

  it('ends in information while the start waits for a saved change', async () => {
    await configure(scenario({ runNow: { projects: { outcome: 'deferred' } } }));

    const { dialog } = await runNow();

    const receipt = dialog.querySelector('[data-receipt] .message') as HTMLElement;
    expect(receipt.classList).toContain('info');
    expect(text(receipt)).toBe('The start is recorded and goes ahead as soon as the change it belongs to is saved.');
    expect(dialog.querySelector('[data-open-conversation]')).toBeNull();
  });

  it('shows a refused start with its registered text', async () => {
    await configure(scenario({ runNow: { projects: { dailyLimit: 1 } } }));
    expect((await call('POST', 'threads/start', { agentId: 'projects', subjectType: 'project', subjectId: 'p-2' })).status).toBe(202);

    const { dialog } = await runNow();

    expect(dialog.querySelector('[data-receipt]')).toBeNull();
    expect(text(dialog.querySelector('[data-run-now-error]'))).toBe('This agent already ran as often as it may today for this object. Try again later.');
  });

  it('shows the receipt of a restart in the run timeline', async () => {
    await configure();
    const conversationId = (await call('GET', 'threads?agentId=projects&subjectType=project&subjectId=p-1')).body.thread.conversationId;
    expect((await call('POST', 'admin/agents/projects/state', { state: 'paused', reason: null })).status).toBe(200);
    const { fixture, element } = await render(NhAssistantRunTimelineComponent, { conversationId, role: 'approver' });

    button(element, 'Start again').click();
    await settle(fixture);

    const receipt = element.querySelector('nh-assistant-run-receipt .message') as HTMLElement;
    expect(receipt.classList).toContain('warning');
    expect(text(receipt)).toBe('The agent is paused. This run starts when an administrator resumes it.');
  });
});

describe('The failure of a run', () => {
  it('reads as its registered text, or the generic text, wherever the run shows', async () => {
    await configure();

    const objectAgents = await render(ObjectPageHost);
    objectAgents.fixture.componentInstance.subjectId.set('p-1');
    const object = await settle(objectAgents.fixture);
    expect(text(object.querySelector('[data-latest-run]'))).toContain(rolledBack);

    const result = await render(NhAssistantRunResultComponent, { runId: 'run-rolled-back' });
    expect(text(result.element.querySelector('[data-run-failure]'))).toBe(rolledBack);

    const conversationId = (await call('GET', 'threads?agentId=projects&subjectType=project&subjectId=p-1')).body.thread.conversationId;
    const timeline = await render(NhAssistantRunTimelineComponent, { conversationId });
    expect(text(timeline.element.querySelector('[data-run-failure]'))).toBe(rolledBack);

    const history = await render(NhAssistantAgentRunHistoryComponent, { agentId: 'projects' });
    const failures = Array.from(history.element.querySelectorAll('[data-run-failure]')).map(text);
    expect(failures).toEqual([rolledBack, 'Something went wrong. Please try again.']);
  });
});

describe('The object agents of an invalid object', () => {
  it('show the code of the refusal instead of a load failure', async () => {
    await configure();
    const host = TestBed.createComponent(ObjectPageHost);
    host.componentInstance.subjectId.set(' p-1');
    const element = await settle(host);

    const alert = element.querySelector('[data-object-agents-error]') as HTMLElement;
    expect(text(alert)).toBe('This is not a valid reference to an object. Check the kind of object and its id.');
    expect(alert.querySelector('button')).toBeNull();
  });
});

describe('A download of a run file that fails', () => {
  it('names the reason with its code', async () => {
    const detail: AgentRunResult = {
      run: {
        id: 'run-1', conversationId: 'thread-1', agentId: 'projects', agentDisplayNameKey: 'Project assistant', subjectType: 'project',
        subjectId: 'p-1', subjectDisplayName: 'PRJ-1', triggerId: 'manual', status: 'completed', failureClass: null, failureCode: null,
        kind: 'model', skill: null, initiatorActorId: null, toolCalls: 0, modelCalls: 0, inputTokens: 0, outputTokens: 0, proposalCount: 0,
        result: null, resultTruncated: false, shadow: false, createdAt: '2026-10-18T10:00:00Z', startedAt: null, endedAt: null
      },
      result: 'Done.',
      resultTruncated: false,
      summary: null,
      proposals: [],
      hiddenProposals: 0,
      artifacts: [{ id: 'file-1', name: 'report', version: 1, contentType: 'text/plain', sizeBytes: 4, createdAt: '2026-10-18T10:00:00Z', runId: 'run-1', restricted: false }],
      canOpenConversation: false
    };
    const api = {
      getRunResult: () => of(detail),
      downloadRunArtifact: () => Promise.reject(new NhAssistantApiError(404, 'assistant-artifact-not-found', 'nh-assistant.errors.assistant-artifact-not-found'))
    };
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        { provide: NhAssistantAgentRunApiService, useValue: api }
      ]
    });
    const translate = TestBed.inject(TranslateService);
    await firstValueFrom(translate.use('en'));
    const { fixture, element } = await render(NhAssistantRunResultComponent, { runId: 'run-1' });

    button(element, 'Download').click();
    await settle(fixture);

    expect(text(element.querySelector('[data-download-error]'))).toBe(translate.instant('nh-assistant.errors.assistant-artifact-not-found'));
    expect(fixture.componentInstance.downloadFailed()).toBeTrue();
  });
});

describe('The agents page without the run catalog', () => {
  it('says why, offers no run now and keeps what agents work on, until the catalog is read again', async () => {
    const backend = await configure();
    const original = backend.fetch;
    let refuse = true;
    spyOn(backend, 'fetch').and.callFake((input: string, init: RequestInit) => refuse && input.endsWith('/admin/run-catalog')
      ? Promise.resolve(new Response(JSON.stringify({ code: 'assistant-forbidden', messageKey: 'nh-assistant.errors.assistant-forbidden' }), { status: 403 }))
      : original(input, init));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    element.style.display = 'block';
    element.style.width = '375px';
    await settle(fixture);

    const notice = element.querySelector('[data-catalog-error]') as HTMLElement;
    expect(notice.getAttribute('role')).toBe('alert');
    // At 375px the notice and its action wrap instead of scrolling sideways.
    expect(notice.scrollWidth).toBeLessThanOrEqual(notice.clientWidth + 1);
    expect(element.scrollWidth).toBeLessThanOrEqual(element.clientWidth + 1);
    expect(text(notice)).toContain('The building blocks of agent runs could not be read.');
    expect(text(notice)).toContain(TestBed.inject(TranslateService).instant('nh-assistant.errors.assistant-forbidden'));
    // The agent's page offers no run now, and fits the narrow page as well.
    const page = await openAdminAgent(fixture, element, 'projects', 'build');
    expect(page.querySelector('[data-run-now-agent]')).toBeNull();
    expect(element.scrollWidth).toBeLessThanOrEqual(element.clientWidth + 1);

    // The editor leaves what the agent works on and its groups as they are.
    (page.querySelector('[data-edit-section="profile"]') as HTMLButtonElement).click();
    await settle(fixture);
    const profile = page.querySelector('[data-build-section="profile"]') as HTMLElement;
    expect(profile.querySelector('[data-catalog-unavailable]')).not.toBeNull();
    expect(profile.querySelector('[data-works-on]')).toBeNull();
    button(profile, 'Save').click();
    await settle(fixture);
    const saved = backend.requests.filter(request => request.method === 'PUT' && request.path === 'admin/agents/projects').at(-1)!.body as Record<string, unknown>;
    expect('worksOnSubjectTypes' in saved).toBeFalse();
    expect('worksOnAllSubjects' in saved).toBeFalse();
    expect('perDivision' in saved).toBeFalse();
    expect((await call('GET', 'admin/agents')).body[0].worksOnSubjectTypes).toEqual(['project']);

    refuse = false;
    await backToAdminList(fixture, element);
    (element.querySelector('[data-catalog-retry]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(element.querySelector('[data-catalog-error]')).toBeNull();
    await openAdminAgent(fixture, element, 'projects');
    expect(element.querySelector('[data-run-now-agent="projects"]')).not.toBeNull();
  });
});

describe('Field errors of the server in the administration', () => {
  it('show at the fields of an MCP server, which stop at the lengths the server accepts', async () => {
    await configure(scenario({ admin: { ...scenario().admin!, policies: ['app.files'] } }));
    const { fixture, element } = await render(NhAssistantAdminMcpServersComponent);
    button(element, 'Edit').click();
    await settle(fixture);

    const maxLength = (selector: string) => (element.querySelector(selector) as HTMLInputElement).maxLength;
    expect(maxLength('input[id$="-name"]')).toBe(200);
    expect(maxLength('input[id$="-url"]')).toBe(2048);
    expect(maxLength('input[id$="-policy"]')).toBe(256);
    expect(maxLength('input[id$="-certificate"]')).toBe(100);

    const policy = element.querySelector('input[id$="-policy"]') as HTMLInputElement;
    policy.value = 'app.unknown';
    policy.dispatchEvent(new Event('input'));
    button(element, 'Save').click();
    await settle(fixture);

    expect(policy.getAttribute('aria-invalid')).toBe('true');
    const error = element.querySelector(`#${policy.getAttribute('aria-describedby')!.split(' ').at(-1)}`) as HTMLElement;
    expect(text(error)).toBe('The application does not know this name.');
    expect(document.activeElement).toBe(policy);
    element.style.display = 'block';
    element.style.width = '375px';
    await settle(fixture);
    expect(element.scrollWidth).toBeLessThanOrEqual(element.clientWidth + 1);

    // Changing the field clears its error.
    policy.value = 'app.files';
    policy.dispatchEvent(new Event('input'));
    await settle(fixture);
    expect(policy.getAttribute('aria-invalid')).toBeNull();
  });

  it('show at the fields of a run filter', async () => {
    await configure();
    const { fixture, element } = await render(NhAssistantAgentRunHistoryComponent, {
      agentId: 'projects',
      subjectTypes: [{ id: 'project', name: 'Project', pluralName: 'Projects', entityType: null, entityTypeFull: null, link: null, searchable: true, isSystem: false }]
    });

    const type = element.querySelector('select[id$="-type"]') as HTMLSelectElement;
    type.value = 'project';
    type.dispatchEvent(new Event('change'));
    await settle(fixture);
    const object = element.querySelector('input[id$="-object"]') as HTMLInputElement;
    object.value = 'p'.repeat(201);
    object.dispatchEvent(new Event('input'));
    button(element, 'Show runs').click();
    await settle(fixture);

    expect(object.getAttribute('aria-invalid')).toBe('true');
    const error = element.querySelector(`#${object.getAttribute('aria-describedby')!.split(' ').at(-1)}`) as HTMLElement;
    expect(text(error)).toBe('Not valid here.');
    expect(document.activeElement).toBe(object);
  });
});
