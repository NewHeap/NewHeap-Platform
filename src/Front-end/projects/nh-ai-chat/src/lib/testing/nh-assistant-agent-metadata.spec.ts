import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { AdminAgent } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { NhAssistantAdminAgentEditorComponent } from '../../../admin/src/agents/nh-assistant-admin-agent-editor.component';
import {
  NhAssistantAgentMetadataSlot,
  provideNhAssistantAgentMetadataEditors
} from '../../../admin/src/agents/metadata/nh-assistant-agent-metadata-slot';

/**
 * Agent metadata (MD1 to MD3): the mock API's metadata endpoints with the server's checks and
 * versions, and the agent editor's slot per entry, where the application's own editor shows and saves
 * the value.
 */

/** The application's editor of the cost center: one input and a save button, nothing generic. */
@Component({
  selector: 'test-cost-center-editor',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <input data-cost-center [value]="code()" [disabled]="slot.readOnly()" (input)="code.set($any($event.target).value)" />
    @for (error of slot.fieldErrors('code'); track error) {
      <span data-cost-center-error>{{ error }}</span>
    }
    <button type="button" data-cost-center-save [disabled]="slot.readOnly() || slot.saving()" (click)="slot.save({ code: code() })">Save</button>
    <button type="button" data-cost-center-remove (click)="slot.remove()">Remove</button>
  `
})
class CostCenterEditorComponent {
  readonly slot = inject(NhAssistantAgentMetadataSlot);
  readonly code = signal(((this.slot.entry().value as { code?: string } | null)?.code) ?? '');
}

function scenario(): NhAssistantMockScenario {
  return {
    agents: [{ id: 'planner', version: 1, displayNameKey: 'Planner', descriptionKey: 'Plans.', canMutate: true }],
    turns: [],
    admin: {
      agentMetadata: [
        {
          name: 'cost-center',
          editable: true,
          maxBytes: 100,
          indexBy: 'code',
          fields: ['code', 'owner'],
          validate: value => typeof value['code'] === 'string' && /^CC-\d{4}$/.test(value['code']) ? null : { code: ['unknown-code'] }
        },
        { name: 'team', fields: ['name'] }
      ],
      agents: [
        {
          source: 'code', id: 'planner', displayName: 'Planner', description: 'Plans.', instructions: 'Plan.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          metadata: { 'cost-center': { code: 'CC-1000' }, team: { name: 'Planning' } }
        },
        {
          source: 'admin', id: 'follow-up', displayName: 'Follow-up', description: 'Follows up.', instructions: 'Follow up.',
          toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
          metadata: { 'cost-center': { code: 'CC-2000' } }
        }
      ],
      runCatalog: { triggerEvents: [], subjectSets: [], approvalPolicies: [] }
    }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(administers = true, editors = true): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      provideNhAssistantMockApi(scenario()),
      ...(editors
        ? [provideNhAssistantAgentMetadataEditors({ name: 'cost-center', component: CostCenterEditorComponent, label: 'Cost center' })]
        : [])
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

async function editorFor(agentId: string | null): Promise<{ fixture: ComponentFixture<NhAssistantAdminAgentEditorComponent>; element: HTMLElement; saved: AdminAgent[] }> {
  const admin = TestBed.inject(NhAssistantAdminApiService);
  const agents = await firstValueFrom(admin.getAgents());
  const fixture = TestBed.createComponent(NhAssistantAdminAgentEditorComponent);
  fixture.componentRef.setInput('agent', agentId ? agents.find(agent => agent.id === agentId)! : null);
  fixture.componentRef.setInput('catalog', await firstValueFrom(admin.getRunCatalog()));
  const saved: AdminAgent[] = [];
  fixture.componentInstance.saved.subscribe(agent => saved.push(agent));
  return { fixture, element: await settle(fixture), saved };
}

describe('Agent metadata of the mock API', () => {
  it('checks values, versions an agent created here and overrides an editable entry of an agent in code', async () => {
    await configure();

    expect((await call('GET', 'admin/run-catalog')).body.agentMetadata).toEqual([
      { name: 'cost-center', editable: true, maxBytes: 100, indexed: true },
      { name: 'team', editable: false, maxBytes: 4096, indexed: false }
    ]);
    const code = (await call('GET', 'admin/agents/planner/metadata')).body;
    expect(code.map((entry: any) => [entry.name, entry.source, entry.editable, entry.version])).toEqual([
      ['cost-center', 'code', true, null],
      ['team', 'code', false, null]
    ]);
    expect((await call('PUT', 'admin/agents/planner/metadata/team', { value: { name: 'Finance' } })).body.code).toBe('assistant-metadata-read-only');

    // An override of an agent in code has a version of its own.
    const override = await call('PUT', 'admin/agents/planner/metadata/cost-center', { value: { code: 'CC-3000' }, expectedVersion: null });
    expect([override.status, override.body.source, override.body.version, override.body.indexValue]).toEqual([200, 'override', 1, 'cc-3000']);
    expect(override.body.codeValue).toEqual({ code: 'CC-1000' });
    expect((await call('PUT', 'admin/agents/planner/metadata/cost-center', { value: { code: 'CC-3001' }, expectedVersion: null })).status).toBe(409);

    // An entry of an agent created here makes its next version, after the server's checks.
    const refused = await call('PUT', 'admin/agents/follow-up/metadata/cost-center', { value: { code: 'X', color: 'blue' }, expectedVersion: 1 });
    expect([refused.status, refused.body.errors]).toEqual([400, { value: ['invalid'] }]);
    const invalid = await call('PUT', 'admin/agents/follow-up/metadata/cost-center', { value: { code: 'X' } });
    expect(invalid.body.errors).toEqual({ 'value.code': ['unknown-code'], expectedVersion: ['required'] });
    const saved = await call('PUT', 'admin/agents/follow-up/metadata/team', { value: { name: 'Reporting' }, expectedVersion: 1 });
    expect([saved.status, saved.body.source, saved.body.version, saved.body.agentVersion]).toEqual([200, 'data', 2, 2]);
    expect((await call('GET', 'admin/agents')).body.find((agent: AdminAgent) => agent.id === 'follow-up').version).toBe(2);
    expect((await call('DELETE', 'admin/agents/follow-up/metadata/team?expectedVersion=1')).status).toBe(409);
    const removed = await call('DELETE', 'admin/agents/follow-up/metadata/team?expectedVersion=2');
    expect([removed.body.source, removed.body.agentVersion]).toEqual([null, 3]);
    expect((await call('PUT', 'admin/agents/follow-up/metadata/unknown', { value: {}, expectedVersion: 3 })).body.code).toBe('assistant-metadata-entry-not-found');
  });

  it('refuses people who do not administer', async () => {
    await configure(false);
    expect((await call('GET', 'admin/agents/planner/metadata')).status).toBe(403);
    expect((await call('PUT', 'admin/agents/planner/metadata/cost-center', { value: { code: 'CC-3000' } })).status).toBe(403);
  });
});

describe('nh-assistant-admin-agent-editor application fields', () => {
  it('renders the application\'s editor in the slot of its entry, which saves an override of an agent in code', async () => {
    await configure();
    const { fixture, element } = await editorFor('planner');

    const slot = element.querySelector('[data-agent-metadata-entry="cost-center"]') as HTMLElement;
    expect(text(slot.querySelector('.card-header strong'))).toBe('Cost center');
    expect(slot.querySelector('[data-metadata-source]')!.getAttribute('data-source')).toBe('code');
    const input = slot.querySelector('[data-cost-center]') as HTMLInputElement;
    expect(input.value).toBe('CC-1000');

    // The validator's code reaches the application's editor; a valid value is saved as an override.
    input.value = 'nope';
    input.dispatchEvent(new Event('input'));
    (slot.querySelector('[data-cost-center-save]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(slot.querySelector('[data-cost-center-error]'))).toBe('unknown-code');
    expect(text(slot.querySelector('[data-metadata-status]'))).not.toBe('');
    input.value = 'CC-3000';
    input.dispatchEvent(new Event('input'));
    (slot.querySelector('[data-cost-center-save]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(slot.querySelector('[data-metadata-source]')!.getAttribute('data-source')).toBe('override');
    expect(text(slot.querySelector('[data-metadata-status]'))).toBe('Saved.');
    const requests = TestBed.inject(NhAssistantMockBackend).requests.filter(request => request.method === 'PUT');
    expect(requests.map(request => [request.path, request.body])).toEqual([
      ['admin/agents/planner/metadata/cost-center', { value: { code: 'nope' }, expectedVersion: null }],
      ['admin/agents/planner/metadata/cost-center', { value: { code: 'CC-3000' }, expectedVersion: null }]
    ]);

    // A read-only entry without an editor of the application says so.
    const team = element.querySelector('[data-agent-metadata-entry="team"]') as HTMLElement;
    expect(team.querySelector('[data-metadata-read-only]')).not.toBeNull();
    expect(team.querySelector('[data-metadata-no-editor]')).not.toBeNull();
  });

  it('continues the definition of an agent created here on the version its fields made', async () => {
    await configure();
    const { fixture, element, saved } = await editorFor('follow-up');

    const slot = element.querySelector('[data-agent-metadata-entry="cost-center"]') as HTMLElement;
    expect(slot.querySelector('[data-metadata-source]')!.getAttribute('data-source')).toBe('data');
    const input = slot.querySelector('[data-cost-center]') as HTMLInputElement;
    input.value = 'CC-4000';
    input.dispatchEvent(new Event('input'));
    (slot.querySelector('[data-cost-center-save]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(slot.querySelector('[data-metadata-status]'))).toBe('Saved.');

    (element.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
    expect(saved.length).toBe(1);
    expect(saved[0].version).toBe(3);
    const update = TestBed.inject(NhAssistantMockBackend).requests.find(request => request.method === 'PUT' && request.path === 'admin/agents/follow-up');
    expect((update!.body as { expectedVersion: number }).expectedVersion).toBe(2);
  });

  it('asks to save a new agent first and says when the application shows no editor', async () => {
    await configure(true, false);
    const created = await editorFor(null);
    expect(created.element.querySelector('[data-agent-metadata-save-first]')).not.toBeNull();
    TestBed.resetTestingModule();

    await configure(true, false);
    const { element } = await editorFor('planner');
    expect(element.querySelector('[data-agent-metadata-entry="cost-center"] [data-metadata-no-editor]')).not.toBeNull();
  });
});
