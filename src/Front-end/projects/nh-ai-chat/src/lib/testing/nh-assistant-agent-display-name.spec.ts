import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { NhAssistantAdminAgentEditorComponent } from '../../../admin/src/agents/nh-assistant-admin-agent-editor.component';
import { NhAssistantAgentPackageImportDialogComponent } from '../../../admin/src/agents/agent-packages/nh-assistant-agent-package-import-dialog.component';
import { agentDisplayName } from '../../../admin/src/agents/agent-texts';

/**
 * LS26: an agent in code names itself with a translation key. The editor's title, the import dialog and
 * every other place that names an agent read it through `agentDisplayName`, so the reader sees the
 * translation in their language, and an agent created in the administration what was typed.
 */

const plannerKey = 'nh-assistant.agents.planner.name';

function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'planner', version: 1, displayNameKey: plannerKey, descriptionKey: 'Plans.', canMutate: true }],
    turns: [],
    admin: {
      agents: [
        {
          source: 'code', id: 'planner', displayName: plannerKey, description: 'Plans.', instructions: 'Plan.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true
        },
        {
          source: 'admin', id: 'follow-up', displayName: 'Follow-up desk', description: 'Follows up.', instructions: 'Follow up.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], approvalPolicies: [] }
    }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(): Promise<TranslateService> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario())
    ]
  });
  const translate = TestBed.inject(TranslateService);
  translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en, true);
  translate.setTranslation('nl', NH_ASSISTANT_TRANSLATIONS.nl, true);
  // The application translates its agent in code.
  translate.setTranslation('en', { 'nh-assistant': { agents: { planner: { name: 'Project planner' } } } }, true);
  translate.setTranslation('nl', { 'nh-assistant': { agents: { planner: { name: 'Projectplanner' } } } }, true);
  await firstValueFrom(translate.use('en'));
  return translate;
}

async function settle(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

async function editorFor(agentId: string): Promise<{ fixture: ComponentFixture<NhAssistantAdminAgentEditorComponent>; element: HTMLElement }> {
  const admin = TestBed.inject(NhAssistantAdminApiService);
  const agents = await firstValueFrom(admin.getAgents());
  const fixture = TestBed.createComponent(NhAssistantAdminAgentEditorComponent);
  fixture.componentRef.setInput('agent', agents.find(agent => agent.id === agentId)!);
  fixture.componentRef.setInput('catalog', await firstValueFrom(admin.getRunCatalog()));
  return { fixture, element: await settle(fixture) };
}

describe('The display name of an agent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('titles the editor of an agent in code with its translation, in the reader language', async () => {
    const translate = await configure();
    const { fixture, element } = await editorFor('planner');

    expect(text(element.querySelector('h3'))).toBe('Edit Project planner');
    expect(text(element.querySelector('h3'))).not.toContain(plannerKey);

    await firstValueFrom(translate.use('nl'));
    await settle(fixture);
    expect(text(element.querySelector('h3'))).toBe('Projectplanner bewerken');
  });

  it('titles the editor of an agent created in the administration with the typed name', async () => {
    await configure();
    const { element } = await editorFor('follow-up');

    expect(text(element.querySelector('h3'))).toBe('Edit Follow-up desk');
  });

  it('falls back to the text when an agent in code has no translation and never translates an administrator text', async () => {
    const translate = await configure();

    expect(agentDisplayName(translate, { source: 'code', displayName: plannerKey })).toBe('Project planner');
    expect(agentDisplayName(translate, { source: 'code', displayName: 'nh-assistant.agents.unknown.name' })).toBe('nh-assistant.agents.unknown.name');
    expect(agentDisplayName(translate, { source: 'admin', displayName: plannerKey })).toBe(plannerKey);
  });

  it('names an agent of a package that the host does not have yet through the same helper', async () => {
    await configure();
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const fixture = TestBed.createComponent(NhAssistantAgentPackageImportDialogComponent);
    fixture.componentRef.setInput('agents', await firstValueFrom(admin.getAgents()));
    await settle(fixture);
    fixture.componentInstance.text.set(JSON.stringify({
      format: 'newheap.assistant.agent-package',
      formatVersion: 1,
      package: { id: 'planning', version: 1 },
      agents: [
        { id: 'planner', source: 'code', definition: { displayName: plannerKey } },
        { id: 'scheduler', source: 'code', definition: { displayName: plannerKey } },
        { id: 'desk', source: 'admin', definition: { displayName: 'Planning desk' } },
        { id: 'bare', source: 'admin' }
      ]
    }));

    const dialog = fixture.componentInstance;
    expect(dialog.agentName('planner')).toBe('Project planner');
    expect(dialog.agentName('follow-up')).toBe('Follow-up desk');
    expect(dialog.agentName('scheduler')).toBe('Project planner');
    expect(dialog.agentName('desk')).toBe('Planning desk');
    expect(dialog.agentName('bare')).toBe('bare');
  });
});
