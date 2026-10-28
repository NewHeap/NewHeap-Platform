import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { openAdminAgent, openAdminTab, settleAdmin } from './nh-assistant-admin-navigation';
import { provideNhAssistant } from '../provide-nh-assistant';

/**
 * TC1 in the administration: the agent's activity shows what its compositions and scripts ran, per tool
 * the calls, tool calls, rounds saved and result sizes, and per call the tool calls in order with how
 * each ended, in English and Dutch, also for an agent that works in the chat only.
 */

function scenario(): NhAssistantMockScenario {
  return {
    agents: [],
    turns: [],
    subjectTypes: [{ id: 'project', names: { en: 'Project', nl: 'Project' }, subjects: [] }],
    admin: {
      agents: [
        {
          source: 'code', id: 'chat-helper', displayName: 'Chat helper', description: 'Helps in the chat.', instructions: 'Help.',
          toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true, chatOnly: true
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] }
    }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(language: string): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use(language));
}

async function render<T>(component: Type<T>): Promise<{ fixture: ComponentFixture<T>; element: HTMLElement }> {
  const fixture = TestBed.createComponent(component);
  for (let round = 0; round < 8; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return { fixture, element: fixture.nativeElement as HTMLElement };
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

async function openCompositions(language: string): Promise<HTMLElement> {
  await configure(language);
  const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
  const page = await openAdminAgent(fixture, element, 'chat-helper');
  await openAdminTab(fixture, element, 'activity');
  (page.querySelector('[data-activity="compositions"]') as HTMLButtonElement).click();
  await settleAdmin(fixture);
  return page;
}

describe('Composition activity in the administration', () => {
  it('shows per tool what the calls saved and per call its tool calls in order', async () => {
    const page = await openCompositions('en');

    expect(page.querySelector('[data-activity="compositions"]')?.getAttribute('aria-pressed')).toBe('true');
    const overview = page.querySelector('[data-composition-tool="projects.overview"]');
    expect(text(overview?.querySelector('[data-rounds-saved]'))).toBe('2');
    expect(text(overview)).toContain('1840 characters');
    const calls = Array.from(page.querySelectorAll('[data-composition-call]'));
    expect(calls.length).toBe(2);
    const steps = Array.from(calls[1].querySelectorAll('[data-step]')).map(step => spans(step));
    expect(steps).toEqual(['projects.get Completed', 'projects.list-risks Completed', 'projects.get-people Completed']);
    expect(spans(calls[0].querySelector('[data-step="1"]'))).toBe('projects.flag-risk Awaiting approval');
    expect(text(calls[1].querySelector('[data-call-rounds]'))).toBe('2 model rounds saved');
    expect(page.querySelector('[data-compositions-truncated]')).toBeNull();
  });

  it('speaks Dutch', async () => {
    const page = await openCompositions('nl');

    expect(text(page.querySelector('[data-activity="compositions"]'))).toBe('Composities');
    expect(text(page.querySelector('[data-composition-call] [data-call-rounds]'))).toBe('1 modelrondes bespaard');
  });
});

/** The texts of an element's parts, one space apart. */
function spans(element: Element | null | undefined): string {
  return Array.from(element?.querySelectorAll('span') ?? []).map(span => text(span)).join(' ');
}
