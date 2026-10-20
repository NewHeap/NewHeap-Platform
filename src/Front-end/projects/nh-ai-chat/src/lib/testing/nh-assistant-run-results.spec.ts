import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom, of } from 'rxjs';
import { AgentRunResult } from '../models/assistant-agent-run.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantPanelService } from '../services/nh-assistant-panel.service';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import {
  NhAssistantObjectAgentsComponent,
  NhAssistantRunResultComponent,
  NhAssistantRunTimelineComponent
} from '../../../agent-runs/src/public-api';

/**
 * Results and history (W6): a run's whole result rendered safely with its proposals and files, the
 * thread view's run timeline and an object's agents that open it, the administration's runs view of
 * an agent with its filters, and the mock API that lists runs as the server does.
 */
const unsafeAnswer = "**4** projects are open.\n\n- PRJ-1 Pietje\n- PRJ-2 Puk\n\n<script>window.hacked = true</script><img src=x onerror=\"window.hacked = true\">[home](javascript:alert(1))";

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
        { id: 'p-2', displayName: 'PRJ-2 Puk', role: 'reader' },
        { id: 'p-3', displayName: 'PRJ-3 Secret', readable: false }
      ]
    }],
    runs: [
      {
        id: 'run-application', agentId: 'follow-up', subjectType: 'nh-system', subjectId: 'global', result: unsafeAnswer,
        createdAt: '2026-10-19T07:00:00Z', durationSeconds: 90, proposalCount: 2, toolCalls: 3, tokens: 1200, triggerId: 'project-follow-up-weekly'
      },
      { id: 'run-project', agentId: 'projects', subjectType: 'project', subjectId: 'p-1', result: 'Project one is on track.', createdAt: '2026-10-18T10:00:00Z' },
      { id: 'run-failed', agentId: 'projects', subjectType: 'project', subjectId: 'p-1', status: 'failed', createdAt: '2026-10-17T10:00:00Z' },
      { id: 'run-secret', agentId: 'projects', subjectType: 'project', subjectId: 'p-3', result: 'Secret.', createdAt: '2026-10-16T10:00:00Z' },
      { id: 'run-long', agentId: 'follow-up', subjectType: 'nh-system', subjectId: 'global', result: 'y'.repeat(25_000), createdAt: '2026-10-15T07:00:00Z', durationSeconds: 600 },
      { id: 'run-shadow', agentId: 'follow-up', subjectType: 'nh-system', subjectId: 'global', result: 'In shadow.', createdAt: '2026-10-14T07:00:00Z', shadow: true }
    ],
    runNow: { projects: { result: 'Checked **now**.' } },
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
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: true,
          runPolicy: 'app.project.manage', viewPolicy: 'app.project.view'
        }
      ],
      runCatalog: {
        triggerEvents: [],
        subjectSets: [],
        approvalPolicies: [],
        agentPolicies: [
          { name: 'app.project.manage', description: 'People who manage projects.' },
          { name: 'app.project.view', description: 'People who read projects.' }
        ]
      }
    },
    ...overrides
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(script = scenario(), administers = false): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(script)
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  TestBed.inject(NhAssistantMockBackend).setCanAdminister(administers);
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}

function ids(body: { items: { id: string }[] }): string[] {
  return body.items.map(item => item.id);
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('The run histories of the mock API', () => {
  it('list the runs people may see, newest first, with the server filters and validation', async () => {
    await configure();

    // Runs on readable objects and, for the whole application, by the agent's view policy; the
    // object the person may not read and shadow work stay out.
    const all = (await call('GET', 'runs')).body;
    expect(ids(all)).toEqual(['run-application', 'run-project', 'run-failed', 'run-long']);
    expect(all.total).toBe(4);
    expect(all.limited).toBeFalse();
    const application = all.items[0];
    expect(application.agentDisplayNameKey).toBe('Project follow-up');
    expect(application.subjectDisplayName).toBe('The whole application');
    expect(application.result).toBe(unsafeAnswer);
    expect(application.resultTruncated).toBeFalse();
    // A listing shows the beginning of a long answer.
    expect(all.items[3].result.length).toBe(300);
    expect(all.items[3].resultTruncated).toBeTrue();
    expect(all.items[2].result).toBeNull();
    expect(ids((await call('GET', 'runs?subjectType=project&subjectId=p-1')).body)).toEqual(['run-project', 'run-failed']);
    expect(ids((await call('GET', 'runs?status=failed')).body)).toEqual(['run-failed']);
    expect(ids((await call('GET', 'runs?agentId=follow-up')).body)).toEqual(['run-application', 'run-long']);
    expect(ids((await call('GET', 'runs?from=2026-10-16T00:00:00Z&to=2026-10-18T12:00:00Z')).body)).toEqual(['run-project', 'run-failed']);
    const page = (await call('GET', 'runs?page=2&itemsPerPage=3')).body;
    expect(ids(page)).toEqual(['run-long']);
    expect(page.total).toBe(4);
    for (const invalid of ['status=done', 'subjectId=p-1', 'subjectType=Not_A_Type', 'from=2026-10-18T00:00:00Z&to=2026-10-17T00:00:00Z', 'page=0', 'itemsPerPage=51']) {
      const refused = await call('GET', `runs?${invalid}`);
      expect(refused.status).withContext(invalid).toBe(400);
      expect(refused.body.code).toBe('assistant-run-filter-invalid');
    }

    // One run with its whole answer, cut at 20,000 characters; a run someone may not see is unknown.
    const long = (await call('GET', 'runs/run-long')).body;
    expect(long.result.length).toBe(20_000);
    expect(long.resultTruncated).toBeTrue();
    expect((await call('GET', 'runs/run-secret')).status).toBe(404);
    expect((await call('GET', 'runs/run-shadow')).status).toBe(404);
    expect((await call('GET', 'admin/agents/follow-up/runs')).status).toBe(403);
  });

  it('show runs for the whole application only to people who pass the agent view policy', async () => {
    await configure(scenario({ callerPolicies: [] }));

    expect(ids((await call('GET', 'runs')).body)).toEqual(['run-project', 'run-failed']);
    expect((await call('GET', 'runs/run-application')).body.code).toBe('assistant-run-not-found');
  });

  it('list every run of an agent for administrators, shadow runs marked', async () => {
    await configure(scenario({ callerPolicies: [] }), true);

    const view = (await call('GET', 'admin/agents/follow-up/runs')).body;
    expect(ids(view)).toEqual(['run-application', 'run-long', 'run-shadow']);
    expect(view.items.map((item: { shadow: boolean }) => item.shadow)).toEqual([false, false, true]);
    expect(ids((await call('GET', 'admin/agents/projects/runs?subjectType=project&subjectId=p-3')).body)).toEqual(['run-secret']);
    expect((await call('GET', 'admin/agents/unknown/runs')).body.code).toBe('assistant-agent-not-found');
    expect((await call('GET', 'admin/agents/projects/runs?status=done')).body.code).toBe('assistant-run-filter-invalid');
    // Administrators open every run.
    expect((await call('GET', 'runs/run-shadow')).body.result).toBe('In shadow.');
  });
});

@Component({
  standalone: true,
  imports: [NhAssistantRunResultComponent],
  template: `
    <div class="page" style="width: 375px">
      <nh-assistant-run-result [runId]="runId()" (openConversation)="opened.push($event)" />
    </div>
  `
})
class RunResultHost {
  readonly runId = signal('run-application');
  readonly opened: string[] = [];
}

describe('nh-assistant-run-result', () => {
  async function render(runId: string): Promise<{ fixture: ComponentFixture<RunResultHost>; element: HTMLElement }> {
    const fixture = TestBed.createComponent(RunResultHost);
    fixture.componentInstance.runId.set(runId);
    return { fixture, element: await settle(fixture) };
  }

  it('renders the answer as safe Markdown with what the run worked on, its facts and its conversation', async () => {
    await configure();
    const { fixture, element } = await render('run-application');

    expect(text(element.querySelector('h4'))).toBe('Project follow-up · The whole application');
    const answer = element.querySelector('[data-run-answer]') as HTMLElement;
    expect(answer.querySelector('strong')?.textContent).toBe('4');
    expect(Array.from(answer.querySelectorAll('li')).map(item => item.textContent)).toEqual(['PRJ-1 Pietje', 'PRJ-2 Puk']);
    // Model output never becomes markup: no script, image or script link, and nothing ran.
    expect(answer.querySelector('script')).toBeNull();
    expect(answer.querySelector('img')).toBeNull();
    expect(answer.querySelector('a[href^="javascript"]')).toBeNull();
    expect(answer.textContent).toContain('<script>window.hacked = true</script>');
    expect((window as { hacked?: boolean }).hacked).toBeUndefined();
    expect(text(element.querySelector('[data-run-duration]'))).toBe('90 s');
    expect(element.textContent).toContain('3 tool calls');
    expect(element.textContent).toContain('1200 tokens');
    expect(element.querySelector('[data-run-truncated]')).toBeNull();

    (element.querySelector('[data-run-conversation]') as HTMLButtonElement).click();
    expect(fixture.componentInstance.opened).toEqual([jasmine.stringMatching(/^[0-9a-f-]{36}$/)]);
    // At 375px nothing scrolls sideways.
    const page = element.querySelector('.page') as HTMLElement;
    expect(page.scrollWidth).toBeLessThanOrEqual(page.clientWidth + 1);
  });

  it('says when an answer was cut, when a run kept none, and when a run is not there', async () => {
    await configure();
    const { fixture, element } = await render('run-long');
    expect(element.querySelector('[data-run-truncated]')?.textContent).toContain('The answer was longer; this is its beginning.');
    expect(text(element.querySelector('[data-run-duration]'))).toBe('10 min');

    fixture.componentInstance.runId.set('run-failed');
    await settle(fixture);
    expect(element.querySelector('[data-run-no-answer]')?.textContent).toContain('This run kept no answer.');
    expect(element.textContent).toContain('Failed');

    fixture.componentInstance.runId.set('run-secret');
    await settle(fixture);
    expect(element.querySelector('[data-run-not-found]')?.textContent).toContain('This run does not exist or you may not see it.');
  });

  it('lists the proposals the person may see, counts the others and offers its files', async () => {
    const detail: AgentRunResult = {
      run: {
        id: 'run-1', conversationId: 'c-1', agentId: 'follow-up', agentDisplayNameKey: 'Project follow-up',
        subjectType: 'nh-system', subjectId: 'global', subjectDisplayName: 'The whole application', triggerId: 'manual',
        status: 'completed', failureClass: null, failureCode: null, kind: 'rule', skill: 'complete-projects', initiatorActorId: null,
        toolCalls: 0, modelCalls: 0, inputTokens: 0, outputTokens: 0, proposalCount: 3, result: 'Two proposed.', resultTruncated: false,
        shadow: false, createdAt: '2026-10-19T07:00:00Z', startedAt: '2026-10-19T07:00:00Z', endedAt: '2026-10-19T07:00:05Z'
      },
      result: 'Two proposed.',
      resultTruncated: false,
      summary: 'Summary.',
      proposals: [{
        approvalId: 'a-1', proposalId: 'p-1', proposalHash: 'h', conversationId: 'c-2', runId: 'run-1', agentId: 'follow-up',
        subjectType: 'project', subjectId: 'p-1', subjectDisplayName: 'PRJ-1 Pietje', toolId: 'projects.complete', summary: 'Complete the project.',
        presentation: null, argumentsPreview: '{}', targets: [], status: 'pending', mode: 'deferred', policyId: null, grantSource: 'agent',
        createdAt: '2026-10-19T07:00:01Z', expiresAt: '2026-10-22T07:00:01Z', initiatorActorId: null, decidedByActorId: null, decidedAt: null,
        executionResultCode: null, executedAt: null, placedBy: { conversationId: 'c-1', runId: 'run-1', subjectType: 'nh-system', subjectId: 'global', subjectDisplayName: 'The whole application' }
      }],
      hiddenProposals: 2,
      artifacts: [
        { id: 'f-1', name: 'weekly-report', version: 2, contentType: 'text/plain', sizeBytes: 12, createdAt: '2026-10-19T07:00:04Z', runId: 'run-1', restricted: false },
        { id: 'f-2', name: 'agent-read', version: 1, contentType: 'text/plain', sizeBytes: 5, createdAt: '2026-10-19T07:00:04Z', runId: 'run-1', restricted: true }
      ],
      canOpenConversation: false
    };
    await configure();
    const api = TestBed.inject(NhAssistantAgentRunApiService);
    spyOn(api, 'getRunResult').and.returnValue(of(detail));
    const download = spyOn(api, 'downloadRunArtifact').and.resolveTo(new Blob(['report']));
    const { element } = await render('run-1');

    expect(element.querySelectorAll('nh-assistant-agent-proposals [data-approval-id]').length).toBe(1);
    expect(element.querySelector('[data-hidden-proposals]')?.textContent).toContain('2 more proposals wait at objects you may not see.');
    expect(element.querySelector('[data-run-conversation]')).toBeNull();
    const files = Array.from(element.querySelectorAll<HTMLElement>('[data-run-artifact]'));
    expect(files.map(file => file.querySelector('.file-name')?.textContent)).toEqual(['weekly-report', 'agent-read']);
    expect(files[1].querySelector('button')).toBeNull();
    const downloadButton = files[0].querySelector('button') as HTMLButtonElement;
    expect(downloadButton.getAttribute('aria-label')).toBe('Download weekly-report');
    spyOn(URL, 'createObjectURL').and.returnValue('blob:report');
    spyOn(URL, 'revokeObjectURL');
    spyOn(HTMLAnchorElement.prototype, 'click');
    downloadButton.click();
    await flush();
    expect(download).toHaveBeenCalledOnceWith('run-1', 'f-1');
  });
});

@Component({
  standalone: true,
  imports: [NhAssistantRunTimelineComponent, NhAssistantObjectAgentsComponent],
  template: `
    @if (conversationId(); as id) {
      <nh-assistant-run-timeline [conversationId]="id" role="participant" />
    }
    <nh-assistant-object-agents subjectType="project" subjectId="p-1" />
  `
})
class ThreadHost {
  readonly conversationId = signal<string | null>(null);
}

describe('The results of a thread and of an object', () => {
  it('open from the run timeline and from the latest run of an object agent', async () => {
    await configure();
    const started = (await call('POST', 'threads/start', { agentId: 'projects', subjectType: 'project', subjectId: 'p-1' })).body;
    const fixture = TestBed.createComponent(ThreadHost);
    fixture.componentInstance.conversationId.set(started.conversationId);
    const element = await settle(fixture);

    const timeline = element.querySelector('nh-assistant-run-timeline') as HTMLElement;
    const toggle = timeline.querySelector('[data-toggle-run-result]') as HTMLButtonElement;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    await settle(fixture);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    const shown = timeline.querySelector('nh-assistant-run-result') as HTMLElement;
    expect(document.getElementById(toggle.getAttribute('aria-controls')!)?.contains(shown)).toBeTrue();
    expect(shown.querySelector('[data-run-answer] strong')?.textContent).toBe('now');
    // The timeline is the run's own thread: it offers no way to open it again.
    expect(shown.querySelector('[data-run-conversation]')).toBeNull();

    const agents = element.querySelector('nh-assistant-object-agents') as HTMLElement;
    const card = agents.querySelector('[data-object-agent="projects"]') as HTMLElement;
    (card.querySelector('[data-toggle-result]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(card.querySelector('nh-assistant-run-result [data-run-answer]')?.textContent?.trim()).toBe('Checked now.');
  });
});

describe('A result its rules wrote per culture', () => {
  const english = '**5** projects are open; proposed completing PRJ-1.';
  const dutch = 'Er zijn **5** projecten open; voorgesteld om PRJ-1 te voltooien.';

  /** The follow-up's run for the whole application, as a run in Dutch whose rules wrote it per culture. */
  function localizedScenario(): NhAssistantMockScenario {
    return scenario({
      agentGroups: [{ id: 'project-management', names: { en: 'Project management', nl: 'Projectbeheer' }, agentIds: ['follow-up'] }],
      runs: [{
        id: 'run-localized', agentId: 'follow-up', subjectType: 'nh-system', subjectId: 'global',
        result: dutch, results: { 'nl-NL': dutch, 'en-US': english }, createdAt: '2026-10-19T07:00:00Z'
      }]
    });
  }

  async function callIn(path: string, language: string): Promise<any> {
    const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
      method: 'GET',
      headers: { 'Accept-Language': language }
    });
    return JSON.parse(await response.text());
  }

  it('reads in the reader\'s language in the mock API wherever a result shows', async () => {
    await configure(localizedScenario(), true);

    for (const [language, expected] of [['en', english], ['en-US', english], ['nl', dutch], ['nl-NL', dutch], ['de', dutch]] as const) {
      expect((await callIn('runs/run-localized', language)).result).withContext(language).toBe(expected);
      expect((await callIn('runs', language)).items[0].result).withContext(language).toBe(expected);
      expect((await callIn('admin/agents/follow-up/runs', language)).items[0].result).withContext(language).toBe(expected);
      const agents = (await callIn('subjects/nh-system/global/agents', language)).agents;
      expect(agents.find((agent: { agentId: string }) => agent.agentId === 'follow-up').latestRun.result).withContext(language).toBe(expected);
      const board = await callIn('boards/project-management', language);
      expect(board.agents.find((agent: { agentId: string }) => agent.agentId === 'follow-up').latestRun.result).withContext(language).toBe(expected);
    }
  });

  it('shows an English reader the English result and a Dutch reader the Dutch result', async () => {
    await configure(localizedScenario());
    const translate = TestBed.inject(TranslateService);

    let { element } = await (async () => {
      const fixture = TestBed.createComponent(RunResultHost);
      fixture.componentInstance.runId.set('run-localized');
      return { element: await settle(fixture) };
    })();
    expect(text(element.querySelector('[data-run-answer]'))).toBe('5 projects are open; proposed completing PRJ-1.');

    await firstValueFrom(translate.use('nl'));
    const dutchFixture = TestBed.createComponent(RunResultHost);
    dutchFixture.componentInstance.runId.set('run-localized');
    element = await settle(dutchFixture);
    expect(text(element.querySelector('[data-run-answer]'))).toBe('Er zijn 5 projecten open; voorgesteld om PRJ-1 te voltooien.');
  });
});

describe('The runs view of an agent in the administration', () => {
  async function openRuns(agentId: string): Promise<{ fixture: ComponentFixture<NhAssistantAdminAgentsComponent>; element: HTMLElement }> {
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    host.style.width = '375px';
    let element = await settle(fixture);
    (element.querySelector(`[data-runs-agent="${agentId}"]`) as HTMLButtonElement).click();
    element = await settle(fixture);
    return { fixture, element };
  }

  function select(element: HTMLElement, label: string, value: string): void {
    const field = Array.from(element.querySelectorAll('label')).find(item => item.textContent?.trim() === label);
    const control = document.getElementById(field!.getAttribute('for')!) as HTMLInputElement | HTMLSelectElement;
    control.value = value;
    control.dispatchEvent(new Event(control instanceof HTMLSelectElement || control.type === 'date' ? 'change' : 'input'));
  }

  it('lists every run with its object, result, proposals, usage and duration, and filters them', async () => {
    await configure(scenario(), true);
    const runs = spyOn(TestBed.inject(NhAssistantAdminApiService), 'getAgentRuns').and.callThrough();
    const { fixture, element } = await openRuns('follow-up');

    expect(runs).toHaveBeenCalledWith('follow-up', { page: 1, itemsPerPage: 20 });
    expect(text(element.querySelector('h3'))).toBe('Runs');
    expect(text(element.querySelector('[data-runs-count]'))).toBe('3 of 3 runs');
    const cards = Array.from(element.querySelectorAll<HTMLElement>('[data-run]'));
    expect(cards.map(card => card.dataset['run'])).toEqual(['run-application', 'run-long', 'run-shadow']);
    expect(cards[0].querySelector('[data-run-preview]')?.textContent).toContain('**4** projects are open.');
    expect(cards[0].textContent).toContain('2 proposals');
    expect(cards[0].textContent).toContain('3 tool calls');
    expect(text(cards[0].querySelector('[data-run-duration]'))).toBe('90 s');
    expect(cards[0].textContent).toContain('project-follow-up-weekly');
    expect(cards[2].querySelector('[data-run-shadow]')).not.toBeNull();

    // The filters: the status and the period go to the server; an inverted period is refused at its field.
    select(element, 'Status', 'failed');
    select(element, 'From', '2026-10-18');
    select(element, 'Until (inclusive)', '2026-10-17');
    fixture.detectChanges();
    (element.querySelector('.filters button[type="submit"]') as HTMLButtonElement).click();
    await settle(fixture);
    const to = element.querySelector('input[type="date"][aria-invalid="true"]') as HTMLInputElement;
    expect(to).not.toBeNull();
    expect(document.getElementById(to.getAttribute('aria-describedby')!)?.textContent).toContain('The end of the period comes before its start.');
    expect(document.activeElement).toBe(to);

    select(element, 'Until (inclusive)', '2026-10-19');
    fixture.detectChanges();
    (element.querySelector('.filters button[type="submit"]') as HTMLButtonElement).click();
    await settle(fixture);
    const filtered = runs.calls.mostRecent().args[1]!;
    expect(filtered.status).toBe('failed');
    expect(Date.parse(filtered.from!)).toBe(new Date(2026, 9, 18).getTime());
    // The last day counts in full.
    expect(Date.parse(filtered.to!)).toBe(new Date(2026, 9, 20).getTime());
    expect(element.querySelector('[data-runs-empty]')?.textContent).toContain('No runs match.');

    button(element, 'Clear filters').click();
    await settle(fixture);
    // A run's object narrows the view to it.
    (element.querySelector('[data-run="run-application"] [data-only-object]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(runs.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ subjectType: 'nh-system', subjectId: undefined }));
    expect(element.querySelectorAll('[data-run]').length).toBe(3);

    // At 375px nothing scrolls sideways.
    const host = fixture.nativeElement as HTMLElement;
    expect(host.scrollWidth).toBeLessThanOrEqual(host.clientWidth + 1);
  });

  it('opens a run with its whole result and returns to the list and the run', async () => {
    await configure(scenario(), true);
    const panel = TestBed.inject(NhAssistantPanelService);
    const open = spyOn(panel, 'open');
    const { fixture, element } = await openRuns('follow-up');

    const action = element.querySelector('[data-open-run="run-long"]') as HTMLButtonElement;
    action.click();
    await settle(fixture);
    expect(element.querySelector('nh-assistant-run-result [data-run-truncated]')).not.toBeNull();
    expect(document.activeElement).toBe(element.querySelector('[data-runs-back]'));
    (element.querySelector('nh-assistant-run-result [data-run-conversation]') as HTMLButtonElement).click();
    expect(open).toHaveBeenCalledTimes(1);

    (element.querySelector('[data-runs-back]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(document.activeElement).toBe(element.querySelector('[data-open-run="run-long"]'));

    // Back to the agents, at the runs action of the agent.
    button(element, 'Back to agents').click();
    await settle(fixture);
    expect(document.activeElement).toBe(element.querySelector('[data-runs-agent="follow-up"]'));
  });
});

function button(scope: ParentNode, label: string): HTMLButtonElement {
  const match = Array.from(scope.querySelectorAll('button')).find(item => item.textContent?.trim() === label);
  if (!match) {
    throw new Error(`Button "${label}" not found.`);
  }
  return match;
}
