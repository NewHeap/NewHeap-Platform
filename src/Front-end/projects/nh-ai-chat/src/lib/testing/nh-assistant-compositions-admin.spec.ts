import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantApiError } from '../services/nh-assistant-transport';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { openAdminAgent, openAdminTab, settleAdmin } from './nh-assistant-admin-navigation';

/**
 * TC8 in the administration: the agent page shows what the assistant actually does with compositions
 * and scripts and why, the mock API answers it as the server does, and an agent created here switches
 * them on only within the application's ceiling.
 */
function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects', canMutate: true }],
    turns: [],
    admin: {
      tools: [{ id: 'sample-api.project.list', source: 'bridge', effect: 'read-only', description: 'List projects' }],
      agents: [
        {
          source: 'code', id: 'projects', displayName: 'Project assistant', description: 'Projects', instructions: 'Code instructions.',
          toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          compositions: true, scripts: true
        },
        {
          source: 'admin', id: 'desk', displayName: 'Scripting desk', description: 'Answers with scripts.', instructions: 'Help.',
          toolSelectors: ['sample-api.project.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true,
          scripts: true
        }
      ],
      compositionsAsData: { compositions: false, scripts: true },
      compositions: { installed: ['scripts'], on: { scripts: ['chat', 'run'] } }
    }
  };
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

async function failure(promise: Promise<unknown>): Promise<NhAssistantApiError> {
  return promise.then(() => { throw new Error('Expected a failure.'); }, error => error as NhAssistantApiError);
}

async function render(): Promise<{ fixture: ComponentFixture<NhAssistantAdminAgentsComponent>; element: HTMLElement }> {
  const fixture = TestBed.createComponent(NhAssistantAdminAgentsComponent);
  await settleAdmin(fixture);
  return { fixture, element: fixture.nativeElement as HTMLElement };
}

describe('Compositions and scripts in the administration', () => {
  it('answers the effective state per feature and channel with the reasons the server gives', async () => {
    await configure();
    const admin = TestBed.inject(NhAssistantAdminApiService);

    const code = await firstValueFrom(admin.getAgentCompositionStatus('projects'));
    const scripts = code.features.find(feature => feature.feature === 'scripts')!;
    expect(scripts.on).toBeTrue();
    expect(scripts.reason).toBe('on');
    expect(scripts.channels.map(channel => `${channel.channel}:${channel.reason}`))
      .toEqual(['chat:on', 'run:on', 'consultation:off-by-application', 'hand-off:off-by-application']);
    expect(code.features.find(feature => feature.feature === 'compositions')!.reason).toBe('not-installed');

    const desk = await firstValueFrom(admin.getAgentCompositionStatus('desk'));
    expect(desk.features.find(feature => feature.feature === 'scripts')!.reason).toBe('on');
    expect((await firstValueFrom(admin.getRunCatalog())).compositionCeiling).toEqual({ compositions: false, scripts: true });

    // Switched off for the agent, it says so.
    const agent = (await firstValueFrom(admin.getAgents())).find(item => item.id === 'desk')!;
    await firstValueFrom(admin.updateAgent('desk', { ...agent, scripts: false, expectedVersion: agent.version }));
    expect((await firstValueFrom(admin.getAgentCompositionStatus('desk'))).features.find(feature => feature.feature === 'scripts')!.reason)
      .toBe('off-for-agent');
  });

  it('refuses to switch a feature on outside the ceiling for an agent created here, as the server does', async () => {
    await configure();
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const input = {
      id: 'new-desk', displayName: 'New desk', description: 'Desk.', instructions: 'Help.', toolSelectors: [],
      mcpServerIds: [], requiredPolicy: null, autonomy: 'explain' as const, isEnabled: true
    };

    const refused = await failure(firstValueFrom(admin.createAgent({ ...input, compositions: true })));
    expect(refused.status).toBe(400);
    expect(refused.fieldErrors['compositions']).toEqual(['not-allowed']);

    const created = await firstValueFrom(admin.createAgent({ ...input, scripts: true }));
    expect(created.scripts).toBeTrue();
    expect(created.compositions).toBeFalse();
  });

  it('shows the state on the overview and the setting in the tools of an agent created here', async () => {
    await configure();
    const { fixture, element } = await render();

    const code = await openAdminAgent(fixture, element, 'projects');
    const scripts = code.querySelector('[data-overview-compositions] [data-composition-feature="scripts"]') as HTMLElement;
    expect(scripts.getAttribute('data-on')).toBe('true');
    expect(scripts.textContent).toContain('On: allowed and switched on by the application.');
    expect(Array.from(scripts.querySelectorAll('[data-channel]')).map(item => item.textContent?.trim()))
      .toEqual(['Chat: On', 'Runs: On', 'Consultations: Off', 'Hand-offs: Off']);
    expect(code.querySelector('[data-composition-feature="compositions"]')?.textContent).toContain('The application has no compositions.');

    (code.querySelector('[data-back]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    const desk = await openAdminAgent(fixture, element, 'desk', 'build');
    (desk.querySelector('[data-edit-section="tools"]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    const setting = desk.querySelector('[data-agent-compositions]') as HTMLElement;
    const scriptsBox = setting.querySelector('[data-composition-setting="scripts"] input') as HTMLInputElement;
    const compositionsBox = setting.querySelector('[data-composition-setting="compositions"] input') as HTMLInputElement;
    expect(scriptsBox.checked).toBeTrue();
    expect(scriptsBox.disabled).toBeFalse();
    // The ceiling does not allow compositions for agents created here.
    expect(compositionsBox.disabled).toBeTrue();
    expect(setting.textContent).toContain('The application does not allow this for agents created here.');

    scriptsBox.click();
    (desk.querySelector('[data-build-section="tools"] button[type="submit"]') as HTMLButtonElement).click();
    await settleAdmin(fixture);
    await openAdminTab(fixture, element, 'overview');
    const after = element.querySelector('[data-overview-compositions] [data-composition-feature="scripts"]') as HTMLElement;
    expect(after.getAttribute('data-reason')).toBe('off-for-agent');
    expect(after.textContent).toContain('Off for this agent.');
  });
});
