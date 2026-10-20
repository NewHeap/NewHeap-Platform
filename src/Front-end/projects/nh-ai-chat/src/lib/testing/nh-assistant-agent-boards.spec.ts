import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { AdminAgent, RunCatalog } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantAdminAgentEditorComponent } from '../../../admin/src/agents/nh-assistant-admin-agent-editor.component';
import { NhAssistantAgentBoardComponent } from '../../../agent-runs/src/public-api';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';

/**
 * Groups and boards (W7): the groups of the run catalog, administrators' memberships that win over
 * code, boards that show people the agents they see with their latest result, what is new since their
 * last visit and their history, and the agent editor's groups; in the mock API as on the server.
 */
const unsafeAnswer = '**12** projects are open.\n\n<script>window.hacked = true</script><img src=x onerror="window.hacked = true">';

function scenario(overrides: Partial<NhAssistantMockScenario> = {}): NhAssistantMockScenario {
  return {
    agents: [
      { id: 'statistics', version: 1, displayNameKey: 'Weekly statistics', descriptionKey: 'Counts the projects.', canMutate: true },
      { id: 'regional', version: 1, displayNameKey: 'Regional review', descriptionKey: 'Reviews a division.', canMutate: true },
      { id: 'projects', version: 1, displayNameKey: 'Project assistant', descriptionKey: 'Helps with projects.', canMutate: true }
    ],
    turns: [],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project', nl: 'Project' },
      pluralNames: { en: 'Projects', nl: 'Projecten' },
      subjects: [{ id: 'p-1', displayName: 'PRJ-1 Pietje' }]
    }],
    agentGroups: [
      {
        id: 'project-management',
        names: { en: 'Project management', nl: 'Projectbeheer' },
        descriptions: { en: 'Agents that keep the projects moving.', nl: 'Agents die de projecten op gang houden.' },
        agentIds: ['statistics', 'regional', 'follow-up']
      },
      { id: 'reporting', names: { en: 'Reporting' }, agentIds: ['statistics'] }
    ],
    runs: [
      { id: 'run-statistics', agentId: 'statistics', subjectType: 'nh-system', subjectId: 'global', result: unsafeAnswer, createdAt: '2026-10-19T07:00:00Z' },
      { id: 'run-statistics-old', agentId: 'statistics', subjectType: 'nh-system', subjectId: 'global', result: 'Ten projects were open.', createdAt: '2026-10-12T07:00:00Z' },
      { id: 'run-follow-up', agentId: 'follow-up', subjectType: 'nh-system', subjectId: 'global', result: 'Proposed completing PRJ-1.', createdAt: '2026-10-19T08:00:00Z' }
    ],
    runNow: { statistics: { result: 'Counted **now**.' } },
    admin: {
      agents: [
        {
          source: 'code', id: 'statistics', displayName: 'Weekly statistics', description: 'Counts the projects.', instructions: 'Count.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: true
        },
        {
          source: 'code', id: 'regional', displayName: 'Regional review', description: 'Reviews a division.', instructions: 'Review.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          worksOnSubjectTypes: [], worksOnAllSubjects: false, worksOnApplication: true, perDivision: true
        },
        {
          source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Helps with projects.', instructions: 'Help.',
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
      runCatalog: { triggerEvents: [], subjectSets: [], approvalPolicies: [] }
    },
    callerPolicies: ['app.project.view'],
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
  const translate = TestBed.inject(TranslateService);
  translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en, true);
  await firstValueFrom(translate.use('en'));
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

async function call(method: string, path: string, body?: unknown, language?: string): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    headers: language ? { 'Accept-Language': language } : undefined,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('The groups and boards of the mock API', () => {
  it('list the groups and let administrators decide over code who is in them', async () => {
    await configure(scenario(), true);

    const catalog = (await call('GET', 'admin/run-catalog', undefined, 'nl-NL')).body as RunCatalog;
    expect(catalog.agentGroups).toEqual([
      { id: 'project-management', name: 'Projectbeheer', description: 'Agents die de projecten op gang houden.' },
      { id: 'reporting', name: 'Reporting', description: null }
    ]);

    // Agents in code join groups in code; the follow-up, created as data, was put there.
    const agents = (await call('GET', 'admin/agents')).body as AdminAgent[];
    const statistics = agents.find(agent => agent.id === 'statistics')!;
    expect([statistics.groups, statistics.codeGroups]).toEqual([['project-management', 'reporting'], ['project-management', 'reporting']]);
    const followUp = agents.find(agent => agent.id === 'follow-up')!;
    expect([followUp.groups, followUp.codeGroups]).toEqual([['project-management'], []]);

    // An administrator's choice wins over code; one that equals code lets the code decide again.
    const removed = await call('PUT', 'admin/agents/statistics/groups', { groups: ['reporting'] });
    expect(removed.body).toEqual({ agentId: 'statistics', groups: ['reporting'], codeGroups: ['project-management', 'reporting'] });
    expect((await call('PUT', 'admin/agents/projects/groups', { groups: ['project-management'] })).body.groups).toEqual(['project-management']);
    expect((await call('GET', 'boards/project-management')).body.agents.map((agent: { agentId: string }) => agent.agentId))
      .toEqual(['follow-up', 'projects', 'regional']);

    // The server's validation.
    expect((await call('PUT', 'admin/agents/statistics/groups', { groups: ['unknown'] })).body.errors).toEqual({ groups: ['not-found'] });
    expect((await call('PUT', 'admin/agents/statistics/groups', {})).body.errors).toEqual({ groups: ['required'] });
    expect((await call('PUT', 'admin/agents/unknown/groups', { groups: [] })).status).toBe(404);
    TestBed.inject(NhAssistantMockBackend).setCanAdminister(false);
    expect((await call('PUT', 'admin/agents/statistics/groups', { groups: [] })).status).toBe(403);
  });

  it('show people the boards of the agents they see, with what is new since their visit', async () => {
    await configure(scenario({ callerPolicies: [] }));

    // Without the follow-up's view policy only the agents without one show.
    const boards = (await call('GET', 'boards', undefined, 'nl')).body;
    expect(boards.items).toEqual([
      { id: 'project-management', name: 'Projectbeheer', description: 'Agents die de projecten op gang houden.', agentCount: 2 },
      { id: 'reporting', name: 'Reporting', description: null, agentCount: 1 }
    ]);
    const board = (await call('GET', 'boards/project-management')).body;
    expect(board.scopeKey).toBe('global');
    expect(board.lastVisitedAt).toBeNull();
    expect(board.agents.map((agent: { agentId: string }) => agent.agentId)).toEqual(['regional', 'statistics']);
    const statistics = board.agents[1];
    expect([statistics.subjectType, statistics.subjectId, statistics.latestRun.runId, statistics.isNew]).toEqual(['nh-system', 'global', 'run-statistics', true]);
    expect(statistics.worksOn).toEqual({ allObjects: false, application: true, perDivision: false, subjectTypes: [{ id: 'project', name: 'Project', pluralName: 'Projects' }] });
    expect([board.agents[0].subjectType, board.agents[0].latestRun, board.agents[0].isNew]).toEqual([null, null, false]);

    const visit = (await call('POST', 'boards/project-management/visit')).body;
    expect([visit.groupId, visit.scopeKey]).toEqual(['project-management', 'global']);
    const seen = (await call('GET', 'boards/project-management')).body;
    expect(seen.lastVisitedAt).toBe(visit.visitedAt);
    expect(seen.agents[1].isNew).toBeFalse();

    expect((await call('GET', 'boards/unknown')).body.code).toBe('assistant-board-not-found');
    expect((await call('GET', 'boards/project-management?divisionId=north')).body.code).toBe('assistant-board-scope-invalid');
    expect((await call('POST', 'boards/unknown/visit')).status).toBe(404);
  });
});

@Component({
  standalone: true,
  imports: [NhAssistantAgentBoardComponent],
  template: `
    <div class="page" [style.width.px]="width()">
      <nh-assistant-agent-board [group]="group()" (openConversation)="opened.push($event)" />
    </div>
  `
})
class BoardPageHost {
  readonly group = signal('project-management');
  readonly width = signal(1024);
  readonly opened: string[] = [];
}

describe('nh-assistant-agent-board', () => {
  async function render(width = 1024, group = 'project-management'): Promise<{ fixture: ComponentFixture<BoardPageHost>; element: HTMLElement }> {
    const fixture = TestBed.createComponent(BoardPageHost);
    fixture.componentInstance.width.set(width);
    fixture.componentInstance.group.set(group);
    return { fixture, element: await settle(fixture) };
  }

  function tile(element: HTMLElement, agentId: string): HTMLElement {
    const match = element.querySelector(`[data-board-agent="${agentId}"]`) as HTMLElement | null;
    if (!match) {
      throw new Error(`Agent "${agentId}" not on the board.`);
    }
    return match;
  }

  it('shows the tiles with their latest result safely, marks what is new and records the visit', async () => {
    await configure();
    const { fixture, element } = await render();

    expect(text(element.querySelector('h3'))).toBe('Project management');
    expect(element.querySelector('[data-board-first-visit]')).not.toBeNull();
    expect(Array.from(element.querySelectorAll('[data-board-agent]')).map(item => item.getAttribute('data-board-agent')))
      .toEqual(['follow-up', 'regional', 'statistics']);

    const statistics = tile(element, 'statistics');
    expect(text(statistics.querySelector('[data-works-on]'))).toBe('Works on Projects, the whole application');
    expect(statistics.querySelector('[data-board-new]')).not.toBeNull();
    // The result is model output: Markdown rendered safely, never as HTML.
    const preview = statistics.querySelector('[data-latest-result]') as HTMLElement;
    expect(preview.querySelector('strong')?.textContent).toBe('12');
    expect(preview.querySelector('script')).toBeNull();
    expect(preview.querySelector('img[onerror]')).toBeNull();
    expect((window as unknown as { hacked?: boolean }).hacked).toBeUndefined();
    // A per-division agent has no work for the whole application on this board.
    expect(text(tile(element, 'regional').querySelector('[data-board-no-application]'))).toContain('per division');

    // Showing the board recorded the visit once, after reading it.
    const requests = TestBed.inject(NhAssistantMockBackend).requests.map(request => `${request.method} ${request.path}`);
    const read = requests.indexOf('GET boards/project-management');
    const visited = requests.indexOf('POST boards/project-management/visit');
    expect(read).toBeGreaterThanOrEqual(0);
    expect(visited).toBeGreaterThan(read);
    expect(requests.filter(request => request === 'POST boards/project-management/visit').length).toBe(1);

    // A later read while the board is shown, as after a live update, keeps what was new when it opened.
    const board = fixture.debugElement.query(By.directive(NhAssistantAgentBoardComponent)).componentInstance as NhAssistantAgentBoardComponent;
    board.load();
    await settle(fixture);
    expect(TestBed.inject(NhAssistantMockBackend).requests.filter(request => request.method === 'GET' && request.path === 'boards/project-management').length).toBe(2);
    expect(tile(element, 'statistics').querySelector('[data-board-new]')).not.toBeNull();
    expect(element.querySelector('[data-board-first-visit]')).not.toBeNull();

    // The next visit marks nothing as new.
    TestBed.resetTestingModule();
    await configure();
    await call('POST', 'boards/project-management/visit');
    const again = await render();
    expect(again.element.querySelector('[data-board-new]')).toBeNull();
    expect(again.element.querySelector('[data-board-last-visit]')).not.toBeNull();
  });

  it('runs an agent now, shows its whole result, its history and its proposals', async () => {
    await configure(scenario(), true);
    const { fixture, element } = await render();
    const statistics = tile(element, 'statistics');

    // The whole result on demand.
    const resultToggle = statistics.querySelector('[data-board-toggle-result]') as HTMLButtonElement;
    resultToggle.click();
    await settle(fixture);
    expect(resultToggle.getAttribute('aria-expanded')).toBe('true');
    expect(statistics.querySelector('nh-assistant-run-result')).not.toBeNull();

    // The history lists the runs for the whole application, newest first.
    const historyToggle = statistics.querySelector('[data-board-toggle-history]') as HTMLButtonElement;
    historyToggle.click();
    await settle(fixture);
    expect(statistics.querySelector('nh-assistant-run-result')).toBeNull();
    expect(Array.from(statistics.querySelectorAll('[data-board-history-run]')).map(item => item.getAttribute('data-board-history-run')))
      .toEqual(['run-statistics', 'run-statistics-old']);

    // Run now opens the dialog on the whole application; the started run becomes the latest.
    (statistics.querySelector('[data-board-run-now]') as HTMLButtonElement).click();
    await settle(fixture);
    const dialog = element.querySelector('nh-assistant-run-now-dialog dialog') as HTMLDialogElement;
    expect(dialog.open).toBeTrue();
    (dialog.querySelector('[data-run-now-submit]') as HTMLButtonElement).click();
    await settle(fixture);
    const latest = text(tile(element, 'statistics').querySelector('[data-latest-result]'));
    expect(latest).toBe('Counted now.');
  });

  it('shows that a board is not there and fits at 375px without scrolling sideways', async () => {
    await configure();
    const missing = await render(1024, 'unknown');
    expect(missing.element.querySelector('[data-board-not-found]')).not.toBeNull();

    TestBed.resetTestingModule();
    await configure();
    const { element } = await render(375);
    const page = element.querySelector('.page') as HTMLElement;
    expect(page.scrollWidth).toBeLessThanOrEqual(page.clientWidth + 1);
    for (const item of Array.from(element.querySelectorAll<HTMLElement>('[data-board-agent]'))) {
      expect(item.getBoundingClientRect().right).toBeLessThanOrEqual(page.getBoundingClientRect().right + 0.5);
    }
    // One tile per row on a phone.
    const tiles = Array.from(element.querySelectorAll<HTMLElement>('[data-board-agent]'));
    expect(new Set(tiles.map(item => Math.round(item.getBoundingClientRect().left))).size).toBe(1);

    // Several columns on a wide page.
    TestBed.resetTestingModule();
    await configure();
    const wide = await render(1024);
    const wideTiles = Array.from(wide.element.querySelectorAll<HTMLElement>('[data-board-agent]'));
    expect(new Set(wideTiles.map(item => Math.round(item.getBoundingClientRect().left))).size).toBeGreaterThan(1);
  });
});

describe('nh-assistant-admin-agent-editor groups', () => {
  it('puts an agent in the groups an administrator chooses and shows those of its code', async () => {
    await configure(scenario(), true);
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const agents = await firstValueFrom(admin.getAgents());
    const catalog = await firstValueFrom(admin.getRunCatalog());
    const fixture = TestBed.createComponent(NhAssistantAdminAgentEditorComponent);
    fixture.componentRef.setInput('agent', agents.find(agent => agent.id === 'statistics')!);
    fixture.componentRef.setInput('catalog', catalog);
    const saved: AdminAgent[] = [];
    fixture.componentInstance.saved.subscribe(agent => saved.push(agent));
    const element = await settle(fixture);

    const groups = element.querySelector('[data-agent-groups]') as HTMLElement;
    expect(groups).not.toBeNull();
    const management = groups.querySelector('[data-agent-group="project-management"] input') as HTMLInputElement;
    expect(management.checked).toBeTrue();
    expect(groups.querySelector('[data-agent-group="project-management"] [data-group-in-code]')).not.toBeNull();

    // Leaving the group and saving stores the choice, which wins over code.
    management.click();
    await settle(fixture);
    (element.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
    expect(saved.length).toBe(1);
    expect(saved[0].groups).toEqual(['reporting']);
    expect(saved[0].codeGroups).toEqual(['project-management', 'reporting']);
    const stored = (await firstValueFrom(admin.getAgents())).find(agent => agent.id === 'statistics')!;
    expect(stored.groups).toEqual(['reporting']);
  });

  it('offers no groups when the application registers none', async () => {
    await configure(scenario({ agentGroups: [] }), true);
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const fixture = TestBed.createComponent(NhAssistantAdminAgentEditorComponent);
    fixture.componentRef.setInput('catalog', await firstValueFrom(admin.getRunCatalog()));
    const element = await settle(fixture);
    expect(element.querySelector('[data-agent-groups]')).toBeNull();
  });
});
