import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { NhAssistantRunResultComponent } from '../../../agent-runs/src/public-api';

/**
 * A personal MCP server a background run left out (follow-up F3 of the MCP connections plan): the
 * run records the server and why, its result names them with the reason, and the administration's
 * runs view marks the run.
 */
function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'Project assistant', descriptionKey: 'Helps with projects.', canMutate: true }],
    turns: [],
    subjectTypes: [{
      id: 'project',
      names: { en: 'Project' },
      pluralNames: { en: 'Projects' },
      subjects: [{ id: 'p-1', displayName: 'PRJ-1 Pietje' }]
    }],
    runs: [
      {
        id: 'run-skipped', agentId: 'projects', subjectType: 'project', subjectId: 'p-1', result: 'The calendar was not available.',
        createdAt: '2026-10-19T07:00:00Z',
        mcpConnectionSkips: [
          { serverId: 'team-calendar', serverDisplayName: 'Team calendar', code: 'assistant-mcp-consent-required' },
          { serverId: 'planning', serverDisplayName: 'Planning', code: 'assistant-mcp-initiator-only' }
        ]
      },
      { id: 'run-plain', agentId: 'projects', subjectType: 'project', subjectId: 'p-1', result: 'All fine.', createdAt: '2026-10-18T07:00:00Z' }
    ],
    admin: {
      agents: [{
        source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Helps with projects.', instructions: 'Code.',
        toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
        worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false
      }]
    }
  };
}

async function configure(administers = false): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  TestBed.inject(NhAssistantMockBackend).setCanAdminister(administers);
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve));
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

@Component({
  standalone: true,
  imports: [NhAssistantRunResultComponent],
  template: `
    <div class="page" style="width: 375px">
      <nh-assistant-run-result [runId]="runId()" />
    </div>
  `
})
class RunResultHost {
  readonly runId = signal('run-skipped');
}

describe('Personal MCP servers a run left out', () => {
  it('are part of the run in the mock API', async () => {
    await configure();
    const backend = TestBed.inject(NhAssistantMockBackend);

    const run = await (await backend.fetch('/api/assistant/runs/run-skipped', { method: 'GET' })).json();
    const plain = await (await backend.fetch('/api/assistant/runs/run-plain', { method: 'GET' })).json();

    expect(run.run.mcpConnectionSkips).toEqual([
      { serverId: 'team-calendar', serverDisplayName: 'Team calendar', code: 'assistant-mcp-consent-required' },
      { serverId: 'planning', serverDisplayName: 'Planning', code: 'assistant-mcp-initiator-only' }
    ]);
    expect(plain.run.mcpConnectionSkips).toEqual([]);
  });

  it('are named with their reason in the run result, without sideways scrolling at 375px', async () => {
    await configure();
    const fixture = TestBed.createComponent(RunResultHost);
    const element = await settle(fixture);

    const list = element.querySelector('[data-run-mcp-skips]');
    expect(list).not.toBeNull();
    expect(text(list?.closest('div'))).toContain('Connected services left out');
    expect(text(element.querySelector('[data-run-mcp-skip="team-calendar"]')))
      .toBe('Team calendar: The person this background run acts for has not allowed this MCP server with their own account.');
    expect(text(element.querySelector('[data-run-mcp-skip="planning"]')))
      .toContain('Planning: A run someone asked for uses only that person\'s own connection');
    const page = element.querySelector('.page') as HTMLElement;
    expect(page.scrollWidth).toBeLessThanOrEqual(page.clientWidth + 1);

    fixture.componentInstance.runId.set('run-plain');
    await settle(fixture);
    expect(element.querySelector('[data-run-mcp-skips]')).toBeNull();
  });

  it('mark the run in the administration\'s runs view', async () => {
    await configure(true);
    const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
    let element = await settle(fixture);
    (element.querySelector('[data-runs-agent="projects"]') as HTMLButtonElement).click();
    element = await settle(fixture);

    expect(text(element.querySelector('[data-run="run-skipped"] [data-run-mcp-skips]'))).toBe('2 connected services left out');
    expect(element.querySelector('[data-run="run-plain"] [data-run-mcp-skips]')).toBeNull();
  });
});
