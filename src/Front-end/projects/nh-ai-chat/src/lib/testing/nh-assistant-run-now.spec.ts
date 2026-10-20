import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { NhAssistantObjectAgentsComponent } from '../../../agent-runs/src/public-api';

/**
 * Run now (W4): the administration's "Run now" dialog for the whole application, a division or an
 * object, `nh-assistant-object-agents` on an object's page, and the mock API that checks who may run
 * an agent as the server does.
 */
const north = '7f0c0000-0000-4000-8000-0000000000a1';
const south = '7f0c0000-0000-4000-8000-0000000000b2';
const result = 'Six projects are open; I propose to complete two of them. ' + 'x'.repeat(400);

function scenario(overrides: Partial<NhAssistantMockScenario> = {}): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'Project assistant', descriptionKey: 'Helps with projects.', canMutate: true }],
    turns: [],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project' },
      pluralNames: { en: 'Projects' },
      link: '/projects/{id}',
      subjects: [
        { id: 'p-1', displayName: 'PRJ-1 Pietje' },
        { id: 'p-2', displayName: 'PRJ-2 Puk', role: 'reader' }
      ]
    }, {
      id: 'ticket',
      subjects: [{ id: 'T-1', displayName: 'Printer jam' }]
    }],
    divisions: [{ id: north, name: 'North' }, { id: south, name: 'South' }],
    runNow: { 'follow-up': { result, proposals: 2 } },
    admin: {
      agents: [
        {
          source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Helps with projects.', instructions: 'Code.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false
        },
        {
          source: 'admin', id: 'follow-up', displayName: 'Project follow-up', description: 'Counts the open projects.', instructions: 'Count.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true, offeredInChat: false,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: true, runPolicy: 'app.project.manage'
        },
        {
          source: 'admin', id: 'division-count', displayName: 'Division count', description: 'Counts per division.', instructions: 'Count.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: [], worksOnAllSubjects: false, worksOnApplication: true, perDivision: true
        }
      ],
      runCatalog: {
        triggerEvents: [],
        subjectSets: [],
        approvalPolicies: [],
        divisions: true,
        agentPolicies: [{ name: 'app.project.manage', description: 'People who manage projects.' }]
      }
    },
    ...overrides
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(script = scenario()): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(script)
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function button(scope: ParentNode, text: string): HTMLButtonElement {
  const match = Array.from(scope.querySelectorAll('button')).find(item => item.textContent?.trim() === text);
  if (!match) {
    throw new Error(`Button "${text}" not found.`);
  }
  return match;
}

function labelled(scope: HTMLElement, text: string): HTMLInputElement {
  const label = Array.from(scope.querySelectorAll('label')).find(item => item.textContent?.includes(text));
  if (!label) {
    throw new Error(`Choice "${text}" not found.`);
  }
  return label.querySelector('input') as HTMLInputElement;
}

/** Chooses an option of a picker: opens it, types the query and clicks the option. */
async function pick(fixture: ComponentFixture<unknown>, scope: HTMLElement, selector: string, query: string, label: string): Promise<void> {
  const input = scope.querySelector(selector) as HTMLInputElement;
  input.click();
  input.value = query;
  input.dispatchEvent(new Event('input'));
  fixture.detectChanges();
  await new Promise(resolve => setTimeout(resolve, 300));
  await settle(fixture);
  const option = Array.from(scope.querySelectorAll<HTMLElement>('[role="option"]'))
    .find(item => item.querySelector('.label')?.textContent?.trim() === label);
  if (!option) {
    throw new Error(`Option "${label}" not found.`);
  }
  option.click();
  await settle(fixture);
}

function requestBodies(method: string, path: string): unknown[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

function dialogOf(element: HTMLElement): HTMLDialogElement {
  const dialog = element.querySelector('nh-assistant-run-now-dialog dialog') as HTMLDialogElement | null;
  if (!dialog) {
    throw new Error('The run-now dialog is not open.');
  }
  return dialog;
}

/** Opens the administration's run-now dialog of an agent. */
async function openFromCard(agentId: string): Promise<{ fixture: ComponentFixture<NhAssistantAdminAgentsComponent>; element: HTMLElement; dialog: HTMLDialogElement }> {
  const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
  const element = await settle(fixture);
  (element.querySelector(`[data-run-now-agent="${agentId}"]`) as HTMLButtonElement).click();
  await settle(fixture);
  return { fixture, element, dialog: dialogOf(element) };
}

function type(scope: HTMLElement, selector: string, value: string): void {
  const input = scope.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('Run now in the administration', () => {
  it('runs an agent for the whole application from its card and opens its conversation from the receipt', async () => {
    await configure();
    const panel = TestBed.inject(NhAssistantPanelService);
    const open = spyOn(panel, 'open');
    const { fixture, element, dialog } = await openFromCard('follow-up');

    expect(dialog.open).toBeTrue();
    expect(document.getElementById(dialog.getAttribute('aria-labelledby')!)?.textContent?.trim()).toBe('Run Project follow-up now');
    // The whole application comes first and is chosen; nothing else needs choosing.
    const choices = Array.from(dialog.querySelectorAll<HTMLElement>('[data-target]')).map(item => item.textContent?.replace(/\s+/g, ' ').trim());
    expect(choices).toEqual(['The whole application', 'Projects']);
    expect(labelled(dialog, 'The whole application').checked).toBeTrue();
    expect(dialog.querySelector('[data-whole-application]')?.textContent?.trim()).toBe('The whole application');
    expect(document.activeElement).toBe(labelled(dialog, 'The whole application'));

    type(dialog, 'textarea', 'Count the open projects now.');
    fixture.detectChanges();
    expect(dialog.querySelector('.counter')?.textContent?.trim()).toBe('28/4000');
    (dialog.querySelector('[data-run-now-submit]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(requestBodies('POST', 'threads/start')).toEqual([
      { agentId: 'follow-up', subjectType: 'nh-system', subjectId: 'global', message: 'Count the open projects now.' }
    ]);
    const receipt = dialog.querySelector('[data-receipt]') as HTMLElement;
    expect(receipt.textContent).toContain('The agent starts shortly.');
    expect(receipt.querySelector('[data-receipt-subject]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('On The whole application');
    expect(document.activeElement).toBe(receipt.querySelector('[data-receipt-focus]'));
    // The dialog stays within its width; nothing scrolls sideways.
    expect(dialog.scrollWidth).toBeLessThanOrEqual(dialog.clientWidth + 1);

    (receipt.querySelector('[data-open-conversation]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(open).toHaveBeenCalledOnceWith(jasmine.stringMatching(/^[0-9a-f-]{36}$/));
    expect(element.querySelector('nh-assistant-run-now-dialog')).toBeNull();
  });

  it('runs an agent on an object chosen with the picker and asks for one first', async () => {
    await configure();
    const { fixture, dialog } = await openFromCard('follow-up');

    labelled(dialog, 'Projects').click();
    await settle(fixture);
    const field = dialog.querySelector('[data-subject-field]') as HTMLElement;
    expect(field.querySelector('label')?.textContent?.trim()).toBe('Project');
    (dialog.querySelector('[data-run-now-submit]') as HTMLButtonElement).click();
    await settle(fixture);
    const input = field.querySelector('input') as HTMLInputElement;
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(document.getElementById(input.getAttribute('aria-describedby')!.split(' ').at(-1)!)?.textContent).toContain('Choose what the agent works on.');
    expect(document.activeElement).toBe(input);
    expect(requestBodies('POST', 'threads/start')).toEqual([]);

    await pick(fixture, dialog, '[data-subject-field] input', 'pie', 'PRJ-1 Pietje');
    expect(field.querySelector('.field-error')).toBeNull();
    (dialog.querySelector('[data-run-now-submit]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(requestBodies('POST', 'threads/start')).toEqual([{ agentId: 'follow-up', subjectType: 'project', subjectId: 'p-1', message: null }]);
    expect(dialog.querySelector('[data-receipt-subject]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('On Project PRJ-1 Pietje');
  });

  it('runs a per-division agent in a division found by name', async () => {
    await configure();
    const { fixture, dialog } = await openFromCard('division-count');

    expect(Array.from(dialog.querySelectorAll<HTMLElement>('[data-target]')).map(item => item.textContent?.replace(/\s+/g, ' ').trim()))
      .toEqual(['The whole application In one division']);
    const field = dialog.querySelector('[data-subject-field]') as HTMLElement;
    expect(field.querySelector('label')?.textContent?.trim()).toBe('Division');
    expect((field.querySelector('input') as HTMLInputElement).placeholder).toBe('Search a division by name');
    await pick(fixture, dialog, '[data-subject-field] input', 'so', 'South');
    (dialog.querySelector('[data-run-now-submit]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(requestBodies('POST', 'threads/start')).toEqual([
      { agentId: 'division-count', subjectType: 'nh-system', subjectId: 'division-' + south.replace(/-/g, ''), message: null }
    ]);
    expect(dialog.querySelector('[data-receipt]')?.textContent).toContain('South');
  });

  it('offers only what the agent works on and shows the server refusal in the dialog', async () => {
    await configure(scenario({ callerPolicies: [] }));
    // An agent of objects only is not offered the whole application.
    let opened = await openFromCard('projects');
    expect(Array.from(opened.dialog.querySelectorAll<HTMLElement>('[data-target]')).map(item => item.textContent?.trim())).toEqual(['Projects']);
    button(opened.dialog, 'Cancel').click();
    await settle(opened.fixture);
    expect(opened.element.querySelector('nh-assistant-run-now-dialog')).toBeNull();

    // Without the agent's run policy the server refuses the whole application.
    opened = await openFromCard('follow-up');
    (opened.dialog.querySelector('[data-run-now-submit]') as HTMLButtonElement).click();
    await settle(opened.fixture);
    const error = opened.dialog.querySelector('[data-run-now-error]') as HTMLElement;
    expect(error.getAttribute('role')).toBe('alert');
    expect(error.textContent).toContain('You may not run this agent here.');
    expect(opened.dialog.querySelector('[data-receipt]')).toBeNull();

    // A reader of a project may not run an agent on it.
    labelled(opened.dialog, 'Projects').click();
    await settle(opened.fixture);
    await pick(opened.fixture, opened.dialog, '[data-subject-field] input', 'puk', 'PRJ-2 Puk');
    (opened.dialog.querySelector('[data-run-now-submit]') as HTMLButtonElement).click();
    await settle(opened.fixture);
    expect(opened.dialog.querySelector('[data-run-now-error]')?.textContent).toContain('Your role on this does not allow running the agent.');
  });
});

@Component({
  standalone: true,
  imports: [NhAssistantObjectAgentsComponent],
  template: `
    <div class="page" style="width: 375px">
      <nh-assistant-object-agents [subjectType]="subjectType()" [subjectId]="subjectId()" (openConversation)="opened.push($event)" />
    </div>
  `
})
class ObjectPageHost {
  readonly subjectType = signal('project');
  readonly subjectId = signal('p-1');
  readonly opened: string[] = [];
}

describe('nh-assistant-object-agents', () => {
  async function render(subjectType = 'project', subjectId = 'p-1'): Promise<{ fixture: ComponentFixture<ObjectPageHost>; element: HTMLElement }> {
    const fixture = TestBed.createComponent(ObjectPageHost);
    fixture.componentInstance.subjectType.set(subjectType);
    fixture.componentInstance.subjectId.set(subjectId);
    return { fixture, element: await settle(fixture) };
  }

  function card(element: HTMLElement, agentId: string): HTMLElement {
    const match = element.querySelector(`[data-object-agent="${agentId}"]`) as HTMLElement | null;
    if (!match) {
      throw new Error(`Agent "${agentId}" not listed.`);
    }
    return match;
  }

  it('lists the agents of the object, runs one now and shows its latest result and open proposals', async () => {
    await configure();
    const { fixture, element } = await render();

    // The agents that work on projects; the per-division count works on the whole application only.
    expect(Array.from(element.querySelectorAll('[data-object-agent]')).map(item => item.getAttribute('data-object-agent'))).toEqual(['follow-up', 'projects']);
    const followUp = card(element, 'follow-up');
    expect(followUp.querySelector('strong')?.textContent?.trim()).toBe('Project follow-up');
    expect(followUp.querySelector('[data-never-run]')?.textContent?.trim()).toBe('Not run here yet.');
    expect(followUp.querySelector('[data-open-proposals]')?.textContent?.trim()).toBe('No open proposals');
    expect(followUp.querySelector('[data-toggle-work]')).toBeNull();

    // Run now runs on the object of the page without asking what.
    (followUp.querySelector('[data-object-run-now]') as HTMLButtonElement).click();
    await settle(fixture);
    const dialog = dialogOf(element);
    expect(dialog.querySelector('[data-target]')).toBeNull();
    expect(dialog.querySelector('[data-fixed-subject]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('Runs on Project PRJ-1 Pietje');
    (dialog.querySelector('[data-run-now-submit]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(requestBodies('POST', 'threads/start')).toEqual([{ agentId: 'follow-up', subjectType: 'project', subjectId: 'p-1', message: null }]);
    button(dialog, 'Done').click();
    await settle(fixture);
    expect(element.querySelector('nh-assistant-run-now-dialog')).toBeNull();

    // The list shows the run: its status, the beginning of its result and what it proposes.
    const ran = card(element, 'follow-up');
    expect(ran.querySelector('[data-latest-run] .badge')?.textContent?.trim()).toBe('Completed');
    const shown = ran.querySelector('[data-latest-result]')?.textContent?.trim() ?? '';
    expect(shown.endsWith('…')).toBeTrue();
    expect(shown).toContain(result.slice(0, 300));
    expect(shown).not.toContain(result.slice(0, 301));
    expect(ran.querySelector('[data-open-proposals]')?.textContent?.trim()).toBe('2 open proposals');

    // Its work on the object opens on demand, with the conversation to open in the panel.
    const toggle = ran.querySelector('[data-toggle-work]') as HTMLButtonElement;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    await settle(fixture);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    const work = document.getElementById(toggle.getAttribute('aria-controls')!) as HTMLElement;
    expect(work.querySelector('nh-assistant-agent-thread-view')).not.toBeNull();
    button(work, 'Open conversation').click();
    expect(fixture.componentInstance.opened.length).toBe(1);
    toggle.click();
    await settle(fixture);
    expect(element.querySelector('nh-assistant-agent-thread-view')).toBeNull();

    // At 375px nothing scrolls sideways.
    const page = element.querySelector('.page') as HTMLElement;
    expect(page.scrollWidth).toBeLessThanOrEqual(page.clientWidth + 1);
    for (const item of Array.from(element.querySelectorAll<HTMLElement>('[data-object-agent]'))) {
      expect(item.getBoundingClientRect().right).toBeLessThanOrEqual(page.getBoundingClientRect().right + 0.5);
    }
  });

  it('offers no run to a reader of the object and lists the agents of the whole application', async () => {
    await configure();
    const reader = await render('project', 'p-2');
    expect(reader.element.querySelectorAll('[data-object-agent]').length).toBe(2);
    expect(reader.element.querySelector('[data-object-run-now]')).toBeNull();

    TestBed.resetTestingModule();
    await configure();
    const application = await render('nh-system', 'global');
    // The app-wide follow-up, not the agent that works per division.
    expect(Array.from(application.element.querySelectorAll('[data-object-agent]')).map(item => item.getAttribute('data-object-agent'))).toEqual(['follow-up']);
    expect(application.element.querySelector('[data-object-run-now]')).not.toBeNull();
  });

  it('shows the empty and failed states', async () => {
    await configure();
    const empty = await render('ticket', 'T-1');
    expect(empty.element.querySelector('[data-object-agents-empty]')?.textContent?.trim()).toBe('No agent works on this that you may use.');

    // An object that does not exist reads as such; asking again does not help.
    const missing = await render('project', 'p-404');
    const alert = missing.element.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('This object does not exist, or you may not see it.');
    expect(Array.from(alert.querySelectorAll('button')).filter(item => item.textContent?.trim() === 'Try again')).toEqual([]);
  });
});

describe('provideNhAssistantMockApi run now', () => {
  async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
    const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
      method,
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    return { status: response.status, body: text ? JSON.parse(text) : null };
  }

  it('checks a start as the server does', async () => {
    await configure(scenario({ callerPolicies: [] }));
    const start = (agentId: string, subjectType: string, subjectId: string, message?: string) =>
      call('POST', 'threads/start', { agentId, subjectType, subjectId, message });

    expect((await start('follow-up', 'project', 'p-1', 'x'.repeat(8_001))).body.code).toBe('assistant-message-invalid');
    expect((await start('unknown', 'project', 'p-1')).status).toBe(404);
    expect((await start('follow-up', 'Not_A_Type', 'p-1')).status).toBe(400);
    expect((await start('follow-up', 'order', 'o-1')).body.code).toBe('assistant-subject-type-unknown');
    expect(await start('projects', 'nh-system', 'global')).toEqual(jasmine.objectContaining({ status: 409 }));
    expect((await start('division-count', 'nh-system', 'global')).body.code).toBe('assistant-agent-does-not-work-on');
    expect((await start('follow-up', 'nh-system', 'global')).body.code).toBe('assistant-agent-forbidden');
    expect((await start('follow-up', 'project', 'p-2')).body.code).toBe('assistant-subject-access-denied');
    expect((await start('follow-up', 'project', 'p-404')).status).toBe(403);

    const accepted = await start('follow-up', 'project', 'p-1');
    expect(accepted.status).toBe(202);
    expect(accepted.body).toEqual(jasmine.objectContaining({ triggerId: 'manual', outcome: 'started' }));
    const lookup = await call('GET', 'threads?agentId=follow-up&subjectType=project&subjectId=p-1');
    expect([lookup.body.role, lookup.body.canStart, lookup.body.thread.conversationId]).toEqual(['participant', true, accepted.body.conversationId]);
    const runs = await call('GET', `threads/${accepted.body.conversationId}/runs`);
    expect(runs.body.items.map((run: { triggerId: string; status: string }) => [run.triggerId, run.status])).toEqual([['manual', 'completed']]);
  });

  it('lists the agents of a subject with their latest run and finds divisions by name', async () => {
    await configure();
    await call('POST', 'threads/start', { agentId: 'division-count', subjectType: 'nh-system', subjectId: 'division-' + north.replace(/-/g, '') });

    const division = await call('GET', `subjects/nh-system/division-${north.replace(/-/g, '')}/agents`);
    expect(division.body).toEqual(jasmine.objectContaining({ subjectDisplayName: 'North', role: 'approver' }));
    expect(division.body.agents.map((agent: { agentId: string; canStart: boolean; latestRun: { status: string } | null }) => [agent.agentId, agent.canStart, agent.latestRun?.status]))
      .toEqual([['division-count', true, 'completed']]);
    expect((await call('GET', 'subjects/project/p-404/agents')).status).toBe(404);
    expect((await call('GET', 'subjects/order/o-1/agents')).body.code).toBe('assistant-subject-type-unknown');

    const found = await call('GET', 'subjects/nh-system/search?query=th');
    expect(found.body.items.map((item: { displayName: string }) => item.displayName)).toEqual(['North', 'South']);
    const catalog = (await call('GET', 'admin/run-catalog')).body;
    expect(catalog.subjectTypes.find((item: { id: string }) => item.id === 'nh-system').searchable).toBeTrue();
  });
});

describe('The forms of the assistant on a page without global box-sizing', () => {
  it('keep their fields within a 375px wide page', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    host.style.width = '375px';
    let element = await settle(fixture);
    // The test page sets no box-sizing of its own, as a bare host page.
    expect(getComputedStyle(document.body).boxSizing).toBe('content-box');
    button(element, 'New agent').click();
    element = await settle(fixture);

    const fields = Array.from(element.querySelectorAll<HTMLElement>('input[type="text"], textarea, select'));
    expect(fields.length).toBeGreaterThan(3);
    const right = host.getBoundingClientRect().right;
    for (const field of fields) {
      expect(getComputedStyle(field).boxSizing).toBe('border-box');
      expect(field.getBoundingClientRect().right).withContext(field.id).toBeLessThanOrEqual(right + 0.5);
    }
  });
});
