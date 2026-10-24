import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockAdminAgent, NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NhAssistantAdminAgentsComponent } from '../../../admin/src/public-api';
import { AgentPackage } from '../models/assistant-agent-package.models';
import { provideNhAssistant } from '../provide-nh-assistant';

/**
 * The agent package screens of the administration (S7-2): the export dialog started from an agent, the
 * import dialog with its check per part, and the fields a package locks, read-only with the package's
 * lock and "Unlink", in the agent editor, the run configuration and the metadata slots.
 */

/** A form flow renders several times; one generous timeout covers it. */
const formFlowTimeout = 20_000;

const planner: NhAssistantMockAdminAgent = {
  source: 'code', id: 'planner', displayName: 'Planner', description: 'Plans projects.', instructions: 'Plan the project.',
  toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'execute', isEnabled: true
};

const desks: NhAssistantMockAdminAgent[] = [
  {
    source: 'admin', id: 'intake-desk', displayName: 'Intake desk', description: 'Takes in new projects.', instructions: 'Review each intake.',
    toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
    worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false,
    runConfiguration: { runSettings: { accountability: 'agent' } }
  },
  {
    source: 'admin', id: 'kickoff-desk', displayName: 'Kickoff desk', description: 'Starts projects.', instructions: 'Start the project.',
    toolSelectors: ['projects.*'], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose', isEnabled: true,
    worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false,
    runConfiguration: { runSettings: { accountability: 'agent' } }
  }
];

/** Both desks as a package another host wrote, with the kickoff desk's instructions and the intake skill's instructions locked. */
function desksPackage(): AgentPackage {
  const worksOn = { worksOnSubjectTypes: ['project'], worksOnAllSubjects: false, worksOnApplication: false };
  return {
    format: 'newheap.assistant.agent-package',
    formatVersion: 1,
    package: { id: 'desks', version: 1, description: 'The intake and kickoff desks.' },
    agents: [
      {
        id: 'intake-desk',
        source: 'admin',
        definition: { displayName: 'Intake desk', description: 'Takes in new projects.', instructions: 'Review each intake.', toolSelectors: ['projects.*'], autonomy: 'propose', ...worksOn },
        runSettings: { accountability: 'service-account' },
        skills: [{ id: 'intake', description: 'Reviews an intake.', instructions: 'Check the intake.' }],
        metadata: { 'cost-center': { code: 'CC-1' } },
        locks: { 'skill:intake': ['instructions'], 'metadata:cost-center': ['value'] }
      },
      {
        id: 'kickoff-desk',
        source: 'admin',
        definition: { displayName: 'Kickoff desk', description: 'Starts projects.', instructions: 'Start the project.', toolSelectors: ['projects.*'], autonomy: 'propose', ...worksOn },
        runSettings: { accountability: 'agent' },
        handOffs: [{ subjectType: 'project', sources: ['intake-desk'], description: 'Projects to start.' }],
        locks: { agent: ['instructions'], 'handOff:project': ['*'] }
      }
    ]
  };
}

function scenario(agents: NhAssistantMockAdminAgent[], imported = false): NhAssistantMockScenario {
  return {
    agents: [{ id: 'planner', version: 1, displayNameKey: 'Planner', descriptionKey: 'Plans projects.', canMutate: true }],
    turns: [],
    subjectTypes: [{ id: 'project', names: { en: 'Project' }, subjects: [] }],
    admin: {
      handOffsAsData: {
        rules: [{ subjectType: 'project', toAgentsCreatedInAdministration: true, fromAgentsCreatedInAdministration: true }],
        maxPerSubjectPerDay: 5
      },
      agentMetadata: [{ name: 'cost-center', editable: true, fields: ['code'] }],
      runCatalog: { triggerEvents: [], subjectSets: [], ruleHandlers: [], approvalPolicies: [], profiles: [] },
      agents,
      ...(imported ? { agentPackages: [{ package: desksPackage(), serviceAccounts: { 'intake-desk': 'ops' } }] } : {})
    }
  };
}

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

async function configure(script: NhAssistantMockScenario): Promise<void> {
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
  for (let round = 0; round < 10; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

async function render<T>(component: Type<T>): Promise<{ fixture: ComponentFixture<T>; element: HTMLElement }> {
  const fixture = TestBed.createComponent(component);
  return { fixture, element: await settle(fixture) };
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function type(scope: Element, selector: string, value: string): void {
  const input = scope.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

async function call(method: string, path: string, body?: unknown): Promise<{ status: number; body: any }> {
  const response = await TestBed.inject(NhAssistantMockBackend).fetch(`/api/assistant/${path}`, {
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const content = await response.text();
  return { status: response.status, body: content ? JSON.parse(content) : null };
}

function requestBodies(method: string, path: string): unknown[] {
  return TestBed.inject(NhAssistantMockBackend).requests
    .filter(item => item.method === method && item.path === path)
    .map(item => item.body);
}

/** The card of an agent on the agents page. */
function card(element: HTMLElement, agentId: string): HTMLElement {
  return Array.from(element.querySelectorAll<HTMLElement>('li.card'))
    .find(item => item.querySelector('.card-header .mono')?.textContent?.trim() === agentId)!;
}

async function click(fixture: ComponentFixture<unknown>, element: Element | null): Promise<HTMLElement> {
  expect(element).withContext('the element to click').not.toBeNull();
  (element as HTMLElement).click();
  return settle(fixture);
}

describe('The export dialog of agent packages', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('starts from an agent, adds the agents its references need, sets locks per field and downloads the package', async () => {
    await configure(scenario([...desks, planner]));
    expect((await call('PUT', 'admin/agents/kickoff-desk/hand-offs/project', { sources: ['intake-desk'] })).status).toBe(200);
    const createObjectURL = spyOn(URL, 'createObjectURL').and.returnValue('blob:package');
    spyOn(URL, 'revokeObjectURL');
    const anchorClick = spyOn(HTMLAnchorElement.prototype, 'click');
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);

    let page = await click(fixture, card(element, 'kickoff-desk').querySelector('[data-export-package="kickoff-desk"]'));
    let dialog = page.querySelector('nh-assistant-agent-package-export-dialog dialog') as HTMLDialogElement;
    expect(dialog.open).toBeTrue();
    expect((dialog.querySelector('[data-export-agent="kickoff-desk"] input') as HTMLInputElement).checked).toBeTrue();
    expect((dialog.querySelector('[data-export-agent="intake-desk"] input') as HTMLInputElement).checked).toBeFalse();
    // The default package id is the first agent's; the parts are read without exporting anything.
    expect((dialog.querySelector('[data-export-package-id]') as HTMLInputElement).placeholder).toBe('kickoff-desk');
    expect(requestBodies('POST', 'admin/agent-packages/export')).toEqual([]);
    expect(Array.from(dialog.querySelectorAll<HTMLElement>('[data-export-agent-locks="kickoff-desk"] [data-export-part]')).map(item => item.dataset['exportPart']))
      .toEqual(['agent', 'runSettings', 'handOff:project']);

    // The hand-off names the intake desk, which is outside the selection: one click takes it along.
    const reference = dialog.querySelector('[data-export-reference="hand-off-source"]');
    expect(text(reference)).toContain('Intake desk hands work to Kickoff desk');
    page = await click(fixture, reference!.querySelector('[data-export-add="intake-desk"]'));
    dialog = page.querySelector('nh-assistant-agent-package-export-dialog dialog') as HTMLDialogElement;
    expect((dialog.querySelector('[data-export-agent="intake-desk"] input') as HTMLInputElement).checked).toBeTrue();
    expect(dialog.querySelector('[data-export-references]')).toBeNull();

    // Lock the kickoff desk's instructions and its hand-off as a whole.
    const agentPart = dialog.querySelector('[data-export-agent-locks="kickoff-desk"] [data-export-part="agent"]') as HTMLElement;
    const mode = agentPart.querySelector('[data-lock-mode]') as HTMLSelectElement;
    mode.value = 'fields';
    mode.dispatchEvent(new Event('change'));
    page = await settle(fixture);
    page.querySelector<HTMLInputElement>('[data-export-agent-locks="kickoff-desk"] [data-export-part="agent"] [data-lock-field="instructions"] input')!.click();
    const handOffMode = page.querySelector('[data-export-agent-locks="kickoff-desk"] [data-export-part="handOff:project"] [data-lock-mode]') as HTMLSelectElement;
    handOffMode.value = 'whole';
    handOffMode.dispatchEvent(new Event('change'));
    type(page, '[data-export-version]', '3');
    page = await settle(fixture);

    page = await click(fixture, page.querySelector('[data-export-download]'));
    expect(requestBodies('POST', 'admin/agent-packages/export')).toEqual([{
      agentIds: ['intake-desk', 'kickoff-desk'],
      options: {
        packageId: null,
        version: 3,
        description: null,
        locks: { 'kickoff-desk': { agent: ['instructions'], 'handOff:project': ['*'] } }
      }
    }]);
    expect(createObjectURL).toHaveBeenCalledTimes(1);
    expect(anchorClick).toHaveBeenCalledTimes(1);
    const blob = createObjectURL.calls.mostRecent().args[0] as Blob;
    const exported = JSON.parse(await blob.text()) as AgentPackage;
    expect(exported.package).toEqual({ id: 'intake-desk', version: 3 });
    expect(exported.agents.find(agent => agent.id === 'kickoff-desk')?.locks).toEqual({ agent: ['instructions'], 'handOff:project': ['*'] });
    expect(text(page.querySelector('[data-export-done]'))).toContain('agent-package-intake-desk.json');
  }, formFlowTimeout);

  it('shows the refusal of the export at its field', async () => {
    await configure(scenario([...desks, planner]));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await click(fixture, card(element, 'intake-desk').querySelector('[data-export-package="intake-desk"]'));

    type(page, '[data-export-package-id]', 'Not An Id');
    page = await settle(fixture);
    expect(text(page.querySelector('[data-export-package-id] + .hint'))).toContain('lowercase');
    page = await click(fixture, page.querySelector('[data-export-download]'));
    expect(requestBodies('POST', 'admin/agent-packages/export')).toEqual([]);
  }, formFlowTimeout);
});

describe('The import dialog of agent packages', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('checks a pasted package per part, asks the technical account, imports it and shows what it did', async () => {
    await configure(scenario([planner]));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await click(fixture, element.querySelector('[data-import-package]'));
    const dialog = page.querySelector('nh-assistant-agent-package-import-dialog dialog') as HTMLDialogElement;
    expect(dialog.open).toBeTrue();

    // Without a package, and with text that is no JSON, the dialog says so before it asks the server.
    page = await click(fixture, dialog.querySelector('[data-import-preview-action]'));
    expect(text(page.querySelector('[data-package-source-error]'))).toContain('Choose a package file or paste a package first.');
    type(page, '[data-package-text]', '{ not json');
    page = await settle(fixture);
    expect(text(page.querySelector('[data-package-source-error]'))).toContain('its JSON cannot be read');

    type(page, '[data-package-text]', JSON.stringify(desksPackage()));
    page = await click(fixture, page.querySelector('[data-import-preview-action]'));
    const intake = page.querySelector('[data-import-agent="intake-desk"]') as HTMLElement;
    expect(text(intake.querySelector('[data-import-part="agent"]'))).toContain('New');
    const settings = intake.querySelector('[data-import-part="runSettings"]') as HTMLElement;
    expect(settings.dataset['outcome']).toBe('refused');
    expect(settings.querySelector('[data-part-field-error="serviceAccountActorId"]')).not.toBeNull();
    expect(text(intake.querySelector('[data-import-part="skill:intake"] [data-part-locks]'))).toContain('Locked: Instructions');
    expect(page.querySelector('[data-import-refused]')).not.toBeNull();
    expect((page.querySelector('[data-import-apply-action]') as HTMLButtonElement).disabled).toBeTrue();

    // The run settings answer to a technical account, which never travels: the import names it.
    type(intake, '[data-import-account] input', 'ops');
    page = await settle(fixture);
    expect(page.querySelector('[data-import-stale]')).not.toBeNull();
    expect((page.querySelector('[data-import-apply-action]') as HTMLButtonElement).disabled).toBeTrue();
    page = await click(fixture, page.querySelector('[data-import-preview-action]'));
    expect(page.querySelector('[data-import-refused]')).toBeNull();
    expect(Array.from(page.querySelectorAll<HTMLElement>('[data-import-part]')).map(row => row.dataset['outcome'])).toEqual(Array(7).fill('created'));

    page = await click(fixture, page.querySelector('[data-import-apply-action]'));
    expect(requestBodies('POST', 'admin/agent-packages/import')).toEqual([{
      package: desksPackage(),
      options: { serviceAccounts: { 'intake-desk': 'ops' }, apply: 'live', relink: null }
    }]);
    expect(text(page.querySelector('[data-import-done]'))).toBe('The package was imported.');
    expect(page.querySelectorAll('[data-import-result] [data-import-part]').length).toBe(7);

    page = await click(fixture, page.querySelector('[data-import-close]'));
    expect(page.querySelector('nh-assistant-agent-package-import-dialog')).toBeNull();
    expect(text(card(page, 'kickoff-desk').querySelector('[data-package-badge]'))).toBe('Package desks 1');
  }, formFlowTimeout);

  it('checks again when the import refuses a part after the check, and shows why per part', async () => {
    await configure(scenario([planner]));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await click(fixture, element.querySelector('[data-import-package]'));
    type(page, '[data-package-text]', JSON.stringify(desksPackage()));
    page = await click(fixture, page.querySelector('[data-import-preview-action]'));
    type(page, '[data-import-agent="intake-desk"] [data-import-account] input', 'ops');
    page = await click(fixture, page.querySelector('[data-import-preview-action]'));
    expect((page.querySelector('[data-import-apply-action]') as HTMLButtonElement).disabled).toBeFalse();

    // Someone creates an agent with the package's id after the check.
    const created = await call('POST', 'admin/agents', {
      id: 'kickoff-desk', displayName: 'Kickoff desk', description: '', instructions: 'Start.', toolSelectors: [], mcpServerIds: [], requiredPolicy: null, autonomy: 'propose'
    });
    expect(created.status).toBe(201);
    page = await click(fixture, page.querySelector('[data-import-apply-action]'));

    expect(text(page.querySelector('[data-import-error]'))).toContain('The check refused parts of the package, so nothing was imported.');
    expect(requestBodies('POST', 'admin/agent-packages/preview').length).toBe(3);
    const agent = page.querySelector('[data-import-agent="kickoff-desk"] [data-import-part="agent"]') as HTMLElement;
    expect(agent.dataset['outcome']).toBe('refused');
    expect(text(agent.querySelector('[data-part-code]'))).toBe('An agent with this id already exists. Choose another id.');
    expect((page.querySelector('[data-import-apply-action]') as HTMLButtonElement).disabled).toBeTrue();
    expect(page.querySelector('[data-import-result]')).toBeNull();
  }, formFlowTimeout);

  it('reads a package file and refuses one above 1 MB before it asks the server', async () => {
    await configure(scenario([planner]));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await click(fixture, element.querySelector('[data-import-package]'));
    const choose = async (file: File): Promise<HTMLElement> => {
      const input = page.querySelector('[data-package-file]') as HTMLInputElement;
      const files = new DataTransfer();
      files.items.add(file);
      input.files = files.files;
      input.dispatchEvent(new Event('change'));
      // Reading the file takes a task of its own.
      await new Promise(resolve => setTimeout(resolve, 20));
      return settle(fixture);
    };

    page = await choose(new File(['x'.repeat(1_048_577)], 'huge.json', { type: 'application/json' }));
    expect(text(page.querySelector('[data-package-source-error]'))).toContain('A package is at most 1 MB.');
    page = await click(fixture, page.querySelector('[data-import-preview-action]'));
    expect(requestBodies('POST', 'admin/agent-packages/preview')).toEqual([]);

    page = await choose(new File([JSON.stringify(desksPackage())], 'desks.json', { type: 'application/json' }));
    expect(text(page.querySelector('[data-file-name]'))).toBe('Read from desks.json.');
    page = await click(fixture, page.querySelector('[data-import-preview-action]'));
    expect(requestBodies('POST', 'admin/agent-packages/preview')).toEqual([{ package: desksPackage(), options: { serviceAccounts: null, apply: 'live', relink: null } }]);
    expect(page.querySelectorAll('[data-import-part]').length).toBe(7);
  }, formFlowTimeout);

  it('shows a package the host does not read with its code, and kept-local parts with their relink choice', async () => {
    await configure(scenario([planner], true));
    expect((await call('POST', 'admin/agents/kickoff-desk/package/unlink', { part: 'handOff:project' })).status).toBe(200);
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await click(fixture, element.querySelector('[data-import-package]'));

    type(page, '[data-package-text]', JSON.stringify({ ...desksPackage(), formatVersion: 2 }));
    page = await click(fixture, page.querySelector('[data-import-preview-action]'));
    expect(text(page.querySelector('[data-import-error]'))).toContain('This is no agent package this version can read');

    const upgrade = desksPackage();
    upgrade.package.version = 2;
    type(page, '[data-package-text]', JSON.stringify(upgrade));
    page = await click(fixture, page.querySelector('[data-import-preview-action]'));
    type(page, '[data-import-agent="intake-desk"] [data-import-account] input', 'ops');
    page = await click(fixture, page.querySelector('[data-import-preview-action]'));
    const handOff = page.querySelector('[data-import-agent="kickoff-desk"] [data-import-part="handOff:project"]') as HTMLElement;
    expect(handOff.dataset['outcome']).toBe('kept-local');
    handOff.querySelector<HTMLInputElement>('[data-relink="handOff:project"] input')!.click();
    page = await settle(fixture);
    page = await click(fixture, page.querySelector('[data-import-preview-action]'));
    expect((page.querySelector('[data-import-agent="kickoff-desk"] [data-import-part="handOff:project"]') as HTMLElement).dataset['outcome']).toBe('unchanged');
    const previews = requestBodies('POST', 'admin/agent-packages/preview') as { options: { relink: unknown } }[];
    expect(previews[previews.length - 1].options.relink).toEqual([{ agentId: 'kickoff-desk', part: 'handOff:project' }]);
  }, formFlowTimeout);
});

describe('Fields an agent package locks', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows the package on the agent, keeps a linked agent from being deleted and its locked definition fields read-only', async () => {
    await configure(scenario([planner], true));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    const kickoff = card(element, 'kickoff-desk');
    expect(text(kickoff.querySelector('[data-package-badge]'))).toBe('Package desks 1');
    expect(text(kickoff.querySelector('[data-package-lock][data-package-part="agent"]'))).toContain('This agent comes from package desks, version 1.');
    expect((kickoff.querySelector('[data-delete-agent]') as HTMLButtonElement).disabled).toBeTrue();
    expect(text(kickoff.querySelector('[data-delete-locked]'))).toContain('Unlink the agent first');

    const editButton = Array.from(kickoff.querySelectorAll<HTMLButtonElement>('.card-actions button')).find(button => text(button) === 'Edit')!;
    const page = await click(fixture, editButton);
    const editor = page.querySelector('nh-assistant-admin-agent-editor') as HTMLElement;
    expect(text(editor.querySelector('[data-package-lock] [data-locked-fields]'))).toBe('Locked: Instructions.');
    expect((editor.querySelector('[data-agent-instructions]') as HTMLFieldSetElement).disabled).toBeTrue();
    expect(editor.querySelector('[data-agent-instructions] [data-locked-mark]')).not.toBeNull();
    expect((editor.querySelector('[id$="-description"]') as HTMLInputElement).disabled).toBeFalse();
    expect((editor.querySelector('[data-agent-tool-selectors]') as HTMLFieldSetElement).disabled).toBeFalse();
  }, formFlowTimeout);

  it('shows locked parts in the run configuration read-only, refuses their removal and unlinks a part with a reason', async () => {
    await configure(scenario([planner], true));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await click(fixture, element.querySelector('[data-run-config-agent="intake-desk"]'));

    const skill = page.querySelector('[data-data-skill="intake"]') as HTMLElement;
    expect(text(skill.querySelector('[data-package-lock][data-package-part="skill:intake"]'))).toContain('Linked to package desks, version 1. Locked: Instructions.');
    expect((skill.querySelector('[data-remove-skill]') as HTMLButtonElement).disabled).toBeTrue();
    expect(text(skill.querySelector('[data-remove-locked]'))).toContain('Package desks locks this part');

    const edit = Array.from(skill.querySelectorAll<HTMLButtonElement>('.card-actions button')).find(button => text(button) === 'Edit')!;
    page = await click(fixture, edit);
    const form = page.querySelector('[data-form="skill"]') as HTMLElement;
    expect((form.querySelector('[data-locked-scope="instructions"]') as HTMLFieldSetElement).disabled).toBeTrue();
    expect((form.querySelector('[id$="-skill-description"]') as HTMLInputElement).disabled).toBeFalse();
    expect(form.querySelector('[data-package-lock] [data-unlink]')).toBeNull();

    // Unlinking makes the skill local: it changes and can be removed again.
    page = await click(fixture, page.querySelector('[data-data-skill="intake"] [data-package-part="skill:intake"] [data-unlink]'));
    type(page, '[data-data-skill="intake"] [data-unlink-reason]', 'Our own wording.');
    page = await click(fixture, page.querySelector('[data-data-skill="intake"] [data-confirm-unlink]'));
    expect(requestBodies('POST', 'admin/agents/intake-desk/package/unlink')).toEqual([{ part: 'skill:intake', reason: 'Our own wording.' }]);
    expect(page.querySelector('[data-data-skill="intake"] [data-package-lock]')).toBeNull();
    expect((page.querySelector('[data-data-skill="intake"] [data-remove-skill]') as HTMLButtonElement).disabled).toBeFalse();
  }, formFlowTimeout);

  it('locks a hand-off as a whole and a metadata value for the application\'s editor', async () => {
    await configure(scenario([planner], true));
    const { fixture, element } = await render(NhAssistantAdminAgentsComponent);
    let page = await click(fixture, element.querySelector('[data-run-config-agent="kickoff-desk"]'));

    const handOff = page.querySelector('[data-hand-offs] [data-hand-off="project"]') as HTMLElement;
    expect(text(handOff.querySelector('[data-package-lock]'))).toContain('The package locks it as a whole');
    expect((handOff.querySelector('[data-remove-hand-off]') as HTMLButtonElement).disabled).toBeTrue();
    const editHandOff = Array.from(handOff.querySelectorAll<HTMLButtonElement>('.card-actions button')).find(button => text(button) === 'Edit')!;
    page = await click(fixture, editHandOff);
    const form = page.querySelector('[data-form="hand-off"]') as HTMLElement;
    expect((form.querySelector('[data-hand-off-sources]') as HTMLFieldSetElement).disabled).toBeTrue();
    expect((form.querySelector('[data-hand-off-limit]') as HTMLInputElement).disabled).toBeTrue();

    const back = TestBed.inject(TranslateService).instant('nh-assistant.admin.run-config.back') as string;
    page = await click(fixture, Array.from(page.querySelectorAll('button')).find(button => text(button) === back) ?? null);
    const intake = card(page, 'intake-desk');
    const editIntake = Array.from(intake.querySelectorAll<HTMLButtonElement>('.card-actions button')).find(button => text(button) === 'Edit')!;
    page = await click(fixture, editIntake);
    const entry = page.querySelector('[data-agent-metadata-entry="cost-center"]') as HTMLElement;
    expect(text(entry.querySelector('[data-package-lock]'))).toContain('Locked: Value.');
  }, formFlowTimeout);
});
