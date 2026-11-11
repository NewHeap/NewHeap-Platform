import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ComponentFixture, DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import {
  ApprovalPart,
  ApprovalPresentationField,
  Conversation,
  ToolCallPart,
  ToolResultPresentation
} from '../../models/assistant-api.models';
import { provideNhAssistant } from '../../provide-nh-assistant';
import { applyNhAssistantEvent } from '../../services/nh-assistant-reducer';
import { NhAssistantApprovalCardComponent } from '../approval-card/nh-assistant-approval-card.component';
import {
  NhAssistantPresentationFieldsComponent,
  nhAssistantFieldChange
} from '../presentation-fields/nh-assistant-presentation-fields.component';
import { NhAssistantThreadComponent } from '../thread/nh-assistant-thread.component';
import { NhAssistantToolResultCardComponent } from './nh-assistant-tool-result-card.component';
import { NhAssistantToolResultComponent } from './nh-assistant-tool-result.component';
import {
  NH_ASSISTANT_TOOL_RESULT_RENDERERS,
  NhAssistantToolResult,
  provideNhAssistantToolResultRenderers
} from './nh-assistant-tool-result-renderers';

const changeFields: ApprovalPresentationField[] = [
  { label: 'Project', value: 'Alpha' },
  { label: 'Status', value: 'On hold', currentValue: 'Active', change: 'changed' },
  { label: 'Owner', value: 'Ann', currentValue: 'Ann', change: 'unchanged' },
  { label: 'Deadline', value: '1 December', currentValue: '', change: 'added' },
  { label: 'Description', value: '', currentValue: 'Launch plan', change: 'cleared' }
];

const projectCard: ToolResultPresentation = {
  title: 'PRJ-1 Alpha',
  summary: 'Active project of the sample division',
  fields: [
    { label: 'Status', value: 'Active' },
    { label: 'Deadline', value: '1 December' }
  ],
  link: { url: '/projects/alpha', label: 'Open project' }
};

function toolCall(overrides: Partial<ToolCallPart> = {}): ToolCallPart {
  return {
    type: 'tool-call',
    invocationId: 'call-1',
    toolId: 'projects.get',
    toolVersion: 1,
    displayName: 'Read project',
    status: 'succeeded',
    argumentsPreview: '{"projectId":"alpha"}',
    resultPreview: '{"id":"alpha","secret":"raw"}',
    resultCode: null,
    resultPresentation: projectCard,
    ...overrides
  };
}

async function configure(extraProviders: unknown[] = [], deferBlockBehavior = DeferBlockBehavior.Playthrough): Promise<void> {
  TestBed.configureTestingModule({
    deferBlockBehavior,
    providers: [
      provideRouter([]),
      provideTranslateService({ fallbackLang: 'en' }),
      provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
      ...(extraProviders as never[])
    ]
  });
  await firstValueFrom(TestBed.inject(TranslateService).use('en'));
}

function rows(element: HTMLElement): HTMLElement[] {
  return Array.from(element.querySelectorAll<HTMLElement>('.field'));
}

/**
 * Renders the fixture with the views that load on first use: the result card and the presentation
 * fields are `@defer` blocks, which render once their chunk has loaded.
 */
async function render(fixture: ComponentFixture<unknown>): Promise<HTMLElement> {
  fixture.detectChanges();
  for (const block of await fixture.getDeferBlocks()) {
    await block.render(DeferBlockState.Complete);
  }
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

/** A renderer of the application: it reads only the injected result. */
@Component({
  selector: 'test-project-result',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="custom" [attr.data-call]="result.invocationId">
      {{ result.toolId }} v{{ result.toolVersion }}: {{ result.presentation().title }} ({{ result.displayName() }})
    </p>
  `
})
class TestProjectResultComponent {
  readonly result = inject(NhAssistantToolResult);
}

@Component({
  selector: 'test-other-result',
  standalone: true,
  template: '<p class="other">other</p>'
})
class TestOtherResultComponent {}

describe('NhAssistantPresentationFieldsComponent', () => {
  let fixture: ComponentFixture<NhAssistantPresentationFieldsComponent>;

  beforeEach(async () => {
    await configure();
    fixture = TestBed.createComponent(NhAssistantPresentationFieldsComponent);
    fixture.componentRef.setInput('fields', changeFields);
    await render(fixture);
  });

  it('shows a plain fact as before and says in words how each field of a change changes', () => {
    const [plain, changed, unchanged, added, cleared] = rows(fixture.nativeElement);

    expect(plain.dataset['change']).toBeUndefined();
    expect(plain.querySelector('dd')!.textContent!.trim()).toBe('Alpha');
    expect(plain.querySelector('[data-change-badge]')).toBeNull();

    expect(changed.querySelector('[data-change-badge]')!.textContent!.trim()).toBe('Changes');
    expect(changed.querySelector('[data-current]')!.textContent).toContain('Now');
    expect(changed.querySelector('[data-current] del')!.textContent!.trim()).toBe('Active');
    expect(changed.querySelector('[data-proposed]')!.textContent).toContain('Proposed');
    expect(changed.querySelector('[data-proposed] ins')!.textContent!.trim()).toBe('On hold');

    expect(unchanged.querySelector('[data-change-badge]')!.textContent!.trim()).toBe('Unchanged');
    expect(unchanged.querySelector('[data-current]')).toBeNull();

    expect(added.querySelector('[data-change-badge]')!.textContent!.trim()).toBe('New value');
    expect(added.querySelector('[data-current]')!.textContent).toContain('Empty');
    expect(added.querySelector('[data-proposed] ins')!.textContent!.trim()).toBe('1 December');

    expect(cleared.querySelector('[data-change-badge]')!.textContent!.trim()).toBe('Cleared');
    expect(cleared.querySelector('[data-current] del')!.textContent!.trim()).toBe('Launch plan');
    expect(cleared.querySelector('[data-proposed]')!.textContent).toContain('Empty');
  });

  it('names a change before and after in a result', () => {
    fixture.componentRef.setInput('mode', 'result');
    fixture.detectChanges();

    const changed = rows(fixture.nativeElement)[1];
    expect(changed.querySelector('[data-change-badge]')!.textContent!.trim()).toBe('Changed');
    expect(changed.querySelector('[data-current]')!.textContent).toContain('Before');
    expect(changed.querySelector('[data-proposed]')!.textContent).toContain('After');
  });

  it('reads the change from the values when a server sends only the current value', () => {
    expect(nhAssistantFieldChange({ label: 'Status', value: 'On hold', currentValue: 'Active' })).toBe('changed');
    expect(nhAssistantFieldChange({ label: 'Status', value: 'Active', currentValue: 'Active' })).toBe('unchanged');
    expect(nhAssistantFieldChange({ label: 'Deadline', value: '1 December', currentValue: '' })).toBe('added');
    expect(nhAssistantFieldChange({ label: 'Deadline', value: '', currentValue: '1 December' })).toBe('cleared');
    expect(nhAssistantFieldChange({ label: 'Project', value: 'Alpha' })).toBeNull();
  });
});

describe('cards that load on first use', () => {
  it('hold the place of the fields with an empty box of about their height until the list has loaded', async () => {
    await configure([], DeferBlockBehavior.Manual);
    const fixture = TestBed.createComponent(NhAssistantPresentationFieldsComponent);
    fixture.componentRef.setInput('fields', changeFields);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    const placeholder = element.querySelector('nh-assistant-card-placeholder') as HTMLElement;
    expect(placeholder.getAttribute('aria-hidden')).toBe('true');
    // Two plain lines and three changes of three lines each, with the gaps between the five fields.
    expect(placeholder.style.minHeight).toBe(`${2 * 20 + 3 * 66 + 4 * 6}px`);
    expect(element.querySelector('dl')).toBeNull();

    const [list] = await fixture.getDeferBlocks();
    await list.render(DeferBlockState.Complete);
    expect(element.querySelector('nh-assistant-card-placeholder')).toBeNull();
    expect(rows(element).length).toBe(5);
  });

  it('hold the place of a result card with an outlined box until the card has loaded', async () => {
    await configure([], DeferBlockBehavior.Manual);
    const fixture = TestBed.createComponent(NhAssistantToolResultCardComponent);
    fixture.componentRef.setInput('presentation', projectCard);
    fixture.componentRef.setInput('displayName', 'Read project');
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    const placeholder = element.querySelector('nh-assistant-card-placeholder') as HTMLElement;
    expect(placeholder.classList).toContain('outlined');
    // Frame and title, the tool's name, the summary, two plain fields and the link.
    expect(placeholder.style.minHeight).toBe(`${46 + 22 + 22 + 10 + 2 * 20 + 6 + 30}px`);
    expect(element.querySelector('article')).toBeNull();
  });
});

describe('NhAssistantApprovalCardComponent with a change', () => {
  it('shows the current and proposed value of each changed field', async () => {
    await configure();
    const approval: ApprovalPart = {
      type: 'approval',
      approvalId: 'approval-1',
      proposalId: 'proposal-1',
      proposalHash: 'hash-1',
      toolId: 'projects.change-status',
      summary: 'Set Alpha on hold',
      presentation: { toolDisplayName: 'Change project status', summary: 'Set Alpha on hold', fields: changeFields, notice: null },
      argumentsPreview: '{}',
      targets: [],
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      status: 'pending'
    };
    const fixture = TestBed.createComponent(NhAssistantApprovalCardComponent);
    fixture.componentRef.setInput('approval', approval);
    await render(fixture);

    const fields = fixture.nativeElement.querySelector('nh-assistant-presentation-fields') as HTMLElement;
    expect(fields).not.toBeNull();
    const status = rows(fields)[1];
    expect(status.querySelector('dt')!.textContent!.trim()).toBe('Status');
    expect(status.querySelector('[data-current] del')!.textContent!.trim()).toBe('Active');
    expect(status.querySelector('[data-proposed] ins')!.textContent!.trim()).toBe('On hold');
    expect(status.textContent).toContain('Changes');

    // An approval of an older server shows its fields as plain facts.
    fixture.componentRef.setInput('approval', {
      ...approval,
      presentation: { ...approval.presentation!, fields: [{ label: 'New status', value: 'On hold' }] }
    });
    fixture.detectChanges();
    const [plain] = rows(fixture.nativeElement);
    expect(plain.querySelector('dd')!.textContent!.trim()).toBe('On hold');
    expect(fixture.nativeElement.querySelector('[data-change-badge]')).toBeNull();
  });
});

describe('NhAssistantToolResultCardComponent', () => {
  it('shows the title, summary, fields and link of the presentation, never the raw result', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantToolResultCardComponent);
    fixture.componentRef.setInput('presentation', {
      ...projectCard,
      fields: [...projectCard.fields, { label: 'Owner', value: 'Bob', currentValue: 'Ann', change: 'changed' }]
    });
    fixture.componentRef.setInput('displayName', 'Read project');
    const element = await render(fixture);

    expect(element.querySelector('article')!.getAttribute('aria-label')).toBe('Result of Read project');
    expect(element.querySelector('.tool')!.textContent!.trim()).toBe('Read project');
    expect(element.querySelector('.title')!.textContent!.trim()).toBe('PRJ-1 Alpha');
    expect(element.querySelector('.summary')!.textContent!.trim()).toBe('Active project of the sample division');
    expect(rows(element).map(row => row.querySelector('dt')!.textContent!.trim())).toEqual(['Status', 'Deadline', 'Owner']);
    // A field the call changed reads before and after.
    expect(rows(element)[2].querySelector('[data-current]')!.textContent).toContain('Before');
    const link = element.querySelector('a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/projects/alpha');
    expect(link.textContent!.trim()).toBe('Open project');
    expect(element.textContent).not.toContain('secret');
  });

  it('leaves out what the presentation does not have', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantToolResultCardComponent);
    fixture.componentRef.setInput('presentation', { title: 'Alpha', summary: null, fields: [], link: null });
    const element = await render(fixture);

    expect(element.querySelector('.tool')).toBeNull();
    expect(element.querySelector('.summary')).toBeNull();
    expect(element.querySelector('nh-assistant-presentation-fields-view')).toBeNull();
    expect(element.querySelector('a')).toBeNull();
    expect(element.querySelector('article')!.getAttribute('aria-label')).toBe('Result of Alpha');
  });
});

describe('NhAssistantToolResultComponent', () => {
  it('renders the application\'s renderer of the tool with the injected presentation', async () => {
    await configure([
      provideNhAssistantToolResultRenderers({ toolId: 'projects.search', component: TestOtherResultComponent }),
      provideNhAssistantToolResultRenderers(
        { toolId: 'projects.get', component: TestOtherResultComponent },
        // The renderer registered last for a tool renders it.
        { toolId: 'projects.get', component: TestProjectResultComponent })
    ]);
    expect(TestBed.inject(NH_ASSISTANT_TOOL_RESULT_RENDERERS).length).toBe(3);
    const fixture = TestBed.createComponent(NhAssistantToolResultComponent);
    fixture.componentRef.setInput('part', toolCall());
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    const custom = element.querySelector('.custom') as HTMLElement;
    expect(custom.textContent!.trim()).toBe('projects.get v1: PRJ-1 Alpha (Read project)');
    expect(custom.dataset['call']).toBe('call-1');
    expect(element.querySelector('nh-assistant-tool-result-card')).toBeNull();
    expect(element.querySelector('.other')).toBeNull();

    // A live update of the presentation reaches the same renderer.
    fixture.componentRef.setInput('part', toolCall({ resultPresentation: { ...projectCard, title: 'PRJ-1 Alpha renamed' } }));
    fixture.detectChanges();
    expect(element.querySelector('.custom')).toBe(custom);
    expect(custom.textContent).toContain('PRJ-1 Alpha renamed');
  });

  it('shows the default card for a tool without a renderer and nothing without a presentation', async () => {
    await configure([provideNhAssistantToolResultRenderers({ toolId: 'projects.search', component: TestOtherResultComponent })]);
    const fixture = TestBed.createComponent(NhAssistantToolResultComponent);
    fixture.componentRef.setInput('part', toolCall());
    const element = await render(fixture);

    expect(element.querySelector('nh-assistant-tool-result-card .title')!.textContent!.trim()).toBe('PRJ-1 Alpha');
    expect(element.querySelector('.other')).toBeNull();

    fixture.componentRef.setInput('part', toolCall({ resultPresentation: null, restricted: true }));
    fixture.detectChanges();
    expect(element.querySelector('nh-assistant-tool-result-card')).toBeNull();
    expect(element.textContent!.trim()).toBe('');
  });

  it('works without any registered renderer', async () => {
    await configure();
    const fixture = TestBed.createComponent(NhAssistantToolResultComponent);
    fixture.componentRef.setInput('part', toolCall({ toolId: 'projects.search' }));
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('nh-assistant-tool-result-card')).not.toBeNull();
  });
});

describe('result cards in the conversation', () => {
  const conversation: Conversation = {
    id: 'conversation-1',
    agentId: 'projects',
    title: null,
    status: 'running',
    createdAt: '2026-11-11T10:00:00Z',
    updatedAt: '2026-11-11T10:00:00Z',
    messages: [{ id: 'assistant-1', role: 'assistant', createdAt: '2026-11-11T10:00:00Z', parts: [toolCall({ status: 'running', resultPresentation: undefined })] }],
    pendingApproval: null
  } as Conversation;

  it('takes the card from tool.completed and keeps one a live event without it does not carry', () => {
    const completed = applyNhAssistantEvent(conversation, {
      type: 'tool.completed',
      data: { invocationId: 'call-1', status: 'succeeded', resultCode: null, resultPreview: '{}', resultPresentation: projectCard }
    });
    const part = completed.messages[0].parts[0] as ToolCallPart;
    expect(part.resultPresentation).toEqual(projectCard);

    const repeated = applyNhAssistantEvent(completed, {
      type: 'tool.completed',
      data: { invocationId: 'call-1', status: 'succeeded', resultCode: null, resultPreview: null }
    });
    expect((repeated.messages[0].parts[0] as ToolCallPart).resultPresentation).toEqual(projectCard);
  });

  it('shows the card of each presented call under the collapsed tool group', async () => {
    await configure([provideNhAssistantToolResultRenderers({ toolId: 'projects.get', component: TestProjectResultComponent })]);
    const fixture = TestBed.createComponent(NhAssistantThreadComponent);
    fixture.componentRef.setInput('messages', [{
      id: 'assistant-1',
      role: 'assistant',
      createdAt: '2026-11-11T10:00:00Z',
      parts: [
        toolCall(),
        toolCall({ invocationId: 'call-2', toolId: 'projects.search', displayName: 'Search projects' }),
        toolCall({ invocationId: 'call-3', toolId: 'projects.list', resultPresentation: undefined })
      ]
    }]);
    const element = await render(fixture);

    expect(element.querySelectorAll('nh-assistant-tool-call-group').length).toBe(1);
    const results = Array.from(element.querySelectorAll<HTMLElement>('nh-assistant-tool-result'));
    expect(results.map(result => result.dataset['toolId'])).toEqual(['projects.get', 'projects.search']);
    expect(results[0].querySelector('.custom')).not.toBeNull();
    expect(results[1].querySelector('nh-assistant-tool-result-card .tool')!.textContent!.trim()).toBe('Search projects');
  });
});
