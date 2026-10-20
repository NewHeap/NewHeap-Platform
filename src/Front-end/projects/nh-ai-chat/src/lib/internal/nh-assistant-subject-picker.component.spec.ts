import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantMockBackend, NhAssistantMockScenario, provideNhAssistantMockApi } from '@newheap/platform-ai-chat/testing';
import { firstValueFrom } from 'rxjs';
import { NH_ASSISTANT_TRANSLATIONS } from '../i18n/nh-assistant-translations';
import { AgentSubjectType } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantChosenSubject, NhAssistantSubjectPickerComponent } from './nh-assistant-subject-picker.component';

const scenario: NhAssistantMockScenario = {
  agents: [{ id: 'projects', version: 1, displayNameKey: 'agents.projects', descriptionKey: 'agents.projects-description', canMutate: true }],
  turns: [],
  subjectTypes: [
    {
      id: 'project',
      names: { en: 'Project', nl: 'Project' },
      pluralNames: { en: 'Projects', nl: 'Projecten' },
      entityType: 'Project',
      entityTypeFull: 'SampleProjectManagement.DAL.Entities.Project',
      link: '/projects/{id}',
      subjects: [
        { id: 'p-1', displayName: 'PRJ-1 — Pietje' },
        { id: 'p-2', displayName: 'PRJ-2 — Puk' },
        { id: 'p-3', displayName: 'PRJ-3 — Hidden', readable: false }
      ]
    },
    { id: 'ticket', subjects: [{ id: 'T-7', displayName: 'Printer jam' }], searchable: false }
  ]
};

@Component({
  standalone: true,
  imports: [NhAssistantSubjectPickerComponent],
  template: `
    <label for="subject">Object</label>
    <nh-assistant-subject-picker inputId="subject" [subjectType]="type()" [value]="value()" (subjectChange)="chosen.push($event)" />
  `
})
class SubjectPickerHost {
  readonly type = signal<AgentSubjectType>({
    id: 'project',
    name: 'Project',
    pluralName: 'Projects',
    entityType: null,
    entityTypeFull: null,
    link: null,
    searchable: true,
    isSystem: false
  });
  readonly value = signal<string | null>(null);
  readonly chosen: (NhAssistantChosenSubject | null)[] = [];
}

describe('NhAssistantSubjectPickerComponent', () => {
  let fixture: ComponentFixture<SubjectPickerHost>;
  let element: HTMLElement;
  let backend: NhAssistantMockBackend;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'mock-token', translations: 'host' }),
        provideNhAssistantMockApi(scenario)
      ]
    });
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('en', NH_ASSISTANT_TRANSLATIONS.en, true);
    await firstValueFrom(translate.use('en'));
    backend = TestBed.inject(NhAssistantMockBackend);
    fixture = TestBed.createComponent(SubjectPickerHost);
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  });

  function field(): HTMLInputElement {
    return element.querySelector('#subject') as HTMLInputElement;
  }

  function type(text: string): void {
    const input = field();
    input.value = text;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** Lets the mock answer: its responses resolve as promises. */
  async function settle(): Promise<void> {
    for (let index = 0; index < 5; index++) {
      await fixture.whenStable();
      await new Promise(resolve => setTimeout(resolve, 0));
      fixture.detectChanges();
    }
  }

  it('searches the readable subjects of a searchable type by name and chooses one with the keyboard', async () => {
    expect(field().getAttribute('role')).toBe('combobox');
    type('pu');
    await new Promise(resolve => setTimeout(resolve, 300));
    await settle();

    const options = Array.from(element.querySelectorAll('[role="option"]')).map(option => option.textContent?.trim());
    expect(options).toEqual(['PRJ-2 — Puk']);
    expect(backend.requests.some(request => request.method === 'GET' && request.path === 'subjects/project/search')).toBeTrue();

    field().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    fixture.detectChanges();
    expect(fixture.componentInstance.chosen).toEqual([{ type: 'project', id: 'p-2', displayName: 'PRJ-2 — Puk' }]);

    // Subjects the caller may not read are never offered.
    type('hidden');
    await new Promise(resolve => setTimeout(resolve, 300));
    await settle();
    expect(element.querySelectorAll('[role="option"]').length).toBe(0);
    expect(element.textContent).toContain('Nothing found.');
  });

  it('asks for the id of a type without search and shows the name the application gives it', async () => {
    fixture.componentInstance.type.set({
      id: 'ticket',
      name: 'ticket',
      pluralName: null,
      entityType: null,
      entityTypeFull: null,
      link: null,
      searchable: false,
      isSystem: false
    });
    fixture.detectChanges();
    expect(field().getAttribute('role')).toBeNull();
    const hint = element.querySelector('.hint')!;
    expect(field().getAttribute('aria-describedby')).toContain(hint.id);
    expect(hint.textContent).toContain('This kind of object is chosen by its id.');

    type('T-7');
    field().dispatchEvent(new Event('change'));
    await settle();
    expect(hint.textContent).toContain('Found: Printer jam');
    expect(fixture.componentInstance.chosen).toEqual([{ type: 'ticket', id: 'T-7', displayName: 'Printer jam' }]);

    // An unknown id shows the reason at the field and clears the choice.
    type('T-8');
    field().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await settle();
    expect(hint.textContent).toContain('No object with this id was found, or you may not see it.');
    expect(field().getAttribute('aria-invalid')).toBe('true');
    expect(fixture.componentInstance.chosen.at(-1)).toBeNull();
  });

  it('lists the subject types in the run catalog in the request language, as the server does', async () => {
    const admin = TestBed.inject(NhAssistantAdminApiService);
    const english = await firstValueFrom(admin.getRunCatalog());
    // The platform's type of the whole application is always listed, by id among the application's.
    expect(english.subjectTypes).toEqual([
      {
        id: 'nh-system',
        name: 'The whole application',
        pluralName: null,
        entityType: null,
        entityTypeFull: null,
        link: null,
        searchable: false,
        isSystem: true
      },
      {
        id: 'project',
        name: 'Project',
        pluralName: 'Projects',
        entityType: 'Project',
        entityTypeFull: 'SampleProjectManagement.DAL.Entities.Project',
        link: '/projects/{id}',
        searchable: true,
        isSystem: false
      },
      { id: 'ticket', name: 'ticket', pluralName: null, entityType: null, entityTypeFull: null, link: null, searchable: false, isSystem: false }
    ]);

    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('nl', NH_ASSISTANT_TRANSLATIONS.nl, true);
    await firstValueFrom(translate.use('nl'));
    const dutch = (await firstValueFrom(admin.getRunCatalog())).subjectTypes!;
    expect(dutch.find(type => type.id === 'project')?.pluralName).toBe('Projecten');
    expect(dutch.find(type => type.id === 'nh-system')?.name).toBe('De hele applicatie');
  });
});

/** The mock API checks searches as the server does. */
describe('the mock subject search', () => {
  let backend: NhAssistantMockBackend;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'mock-token', translations: 'host' }),
        provideNhAssistantMockApi(scenario)
      ]
    });
    backend = TestBed.inject(NhAssistantMockBackend);
  });

  async function get(path: string): Promise<{ status: number; body: any }> {
    const response = await backend.fetch(`/api/assistant/${path}`, { method: 'GET' });
    const text = await response.text();
    return { status: response.status, body: text ? JSON.parse(text) : null };
  }

  it('validates the query, the type and the search like the server', async () => {
    expect(await get('subjects/project/search?query=p')).toEqual({
      status: 400,
      body: { code: 'assistant-validation', messageKey: 'nh-assistant.errors.assistant-validation', errors: { query: ['invalid'] } }
    });
    expect((await get('subjects/order/search?query=prj')).body.code).toBe('assistant-subject-type-unknown');
    expect((await get('subjects/ticket/search?query=printer')).body.code).toBe('assistant-subject-search-unavailable');
    expect((await get('subjects/ticket/search?query=printer')).status).toBe(404);

    const found = await get('subjects/project/search?query=PRJ&limit=1');
    expect(found.status).toBe(200);
    expect(found.body).toEqual({
      subjectType: 'project',
      items: [{ type: 'project', id: 'p-1', displayName: 'PRJ-1 — Pietje', scopeKey: 'global', divisionId: null, link: '/projects/p-1' }]
    });
  });

  it('refuses a scenario the server would refuse at startup', () => {
    for (const subjectTypes of [
      [{ id: 'nh-system' }],
      [{ id: 'project' }, { id: 'project' }],
      [{ id: 'project', names: { nl: 'Project' } }],
      [{ id: 'project', link: '/projects' }]
    ]) {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => null, translations: 'host' }),
          provideNhAssistantMockApi({ ...scenario, subjectTypes })
        ]
      });
      expect(() => TestBed.inject(NhAssistantMockBackend)).withContext(JSON.stringify(subjectTypes)).toThrowError();
    }
  });
});
