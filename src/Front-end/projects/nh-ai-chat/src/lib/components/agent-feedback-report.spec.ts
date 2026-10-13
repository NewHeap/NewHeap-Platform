import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { NhAssistantAgentFeedbackReportComponent } from '@newheap/platform-ai-chat/admin';
import { firstValueFrom, of, throwError } from 'rxjs';
import { AgentFeedbackItem, AgentFeedbackSummaryRow } from '../models/assistant-admin.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAdminApiService } from '../services/nh-assistant-admin-api.service';
import { NhAssistantApiError } from '../services/nh-assistant-transport';

function row(toolId: string, agentVersion: number | null, rejectionRate: number | null): AgentFeedbackSummaryRow {
  return {
    toolId,
    agentVersion,
    pending: 1,
    approved: 6,
    rejected: 4,
    expired: 2,
    withdrawn: 0,
    stale: 0,
    executionFailed: 0,
    rejectionRate,
    rejectedWithFeedback: 3,
    feedbackProposals: 3,
    feedbackAuthors: 2,
    categories: [
      { category: 'other', group: 'other', count: 1 },
      { category: 'not-now', group: 'right-but-not-now', count: 1 },
      { category: 'wrong-value', group: 'wrong', count: 3 },
      { category: 'not-allowed', group: 'not-allowed', count: 2 }
    ]
  };
}

const item: AgentFeedbackItem = {
  id: 'feedback-1',
  approvalId: 'approval-1',
  proposalId: 'proposal-1',
  conversationId: 'thread-1',
  runId: 'run-1',
  scopeKey: 'division:1',
  subjectType: 'project',
  subjectId: 'project-1',
  subjectDisplayName: 'Project One',
  toolId: 'projects.change-status',
  agentVersion: 3,
  approvalStatus: 'rejected',
  summary: 'Agent summary',
  presentation: { toolDisplayName: 'Change project status', summary: 'Move Project One to Active.', fields: [], notice: null },
  category: 'wrong-value',
  group: 'wrong',
  text: 'The deadline is next month.',
  authorActorId: 'user-7',
  bulk: true,
  createdAt: '2026-10-12T10:00:00Z',
  updatedAt: '2026-10-12T10:00:00Z'
};

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

describe('NhAssistantAgentFeedbackReportComponent', () => {
  let admin: jasmine.SpyObj<NhAssistantAdminApiService>;
  let fixture: ComponentFixture<NhAssistantAgentFeedbackReportComponent>;

  beforeEach(async () => {
    admin = jasmine.createSpyObj<NhAssistantAdminApiService>('NhAssistantAdminApiService', [
      'getAgentFeedbackSummary',
      'getAgentFeedback',
      'exportAgentFeedback'
    ]);
    admin.getAgentFeedbackSummary.and.returnValue(of({
      agentId: 'intake',
      from: '2026-09-13T00:00:00Z',
      to: '2026-10-13T00:00:00Z',
      rows: [row('projects.change-status', 3, 0.4), row('projects.request-review', null, null)]
    }));
    admin.getAgentFeedback.and.returnValue(of({ items: [item], total: 1 }));
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' }),
        { provide: NhAssistantAdminApiService, useValue: admin }
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
    fixture = TestBed.createComponent(NhAssistantAgentFeedbackReportComponent);
    fixture.componentRef.setInput('agentId', 'intake');
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();
  });

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function select(id: string, value: string): void {
    const control = element().querySelector(`select[id$="-${id}"]`) as HTMLSelectElement;
    control.value = value;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function exportButton(): HTMLButtonElement {
    return Array.from(element().querySelectorAll('button')).find(button => button.textContent?.includes('Export'))!;
  }

  it('loads the last 30 days and summarizes each tool and agent version', () => {
    const [agentId, range] = admin.getAgentFeedbackSummary.calls.mostRecent().args;
    expect(agentId).toBe('intake');
    expect(Date.parse(range!.to!) - Date.parse(range!.from!)).toBe(30 * 24 * 60 * 60 * 1000);

    const rows = Array.from(element().querySelectorAll('.summary-table tbody tr'));
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('projects.change-status');
    expect(rows[0].textContent).toContain('v3');
    expect(rows[0].textContent).toContain('40%');
    expect(rows[1].textContent).toContain('Consultations');
    expect(rows[1].textContent).toContain('Not decided yet');

    const badges = Array.from(rows[0].querySelectorAll('.badges .badge'));
    expect(badges.map(badge => badge.textContent?.replace(/\s+/g, ' ').trim())).toEqual(['Wrong value 3', 'Not allowed 2', 'Other 1']);
    expect(badges[0].classList).toContain('danger');
    expect(badges[1].classList).toContain('danger');
    expect(badges[2].classList).not.toContain('danger');
    expect(badges[2].classList).not.toContain('warning');
  });

  it('lists the feedback with the proposal as approvers read it', () => {
    const card = element().querySelector('.cards .card') as HTMLElement;

    expect(card.textContent).toContain('Project One');
    expect(card.textContent).toContain('Change project status');
    expect(card.textContent).toContain('The deadline is next month.');
    expect(card.textContent).toContain('Move Project One to Active.');
    expect(card.textContent).toContain('By user-7');
    expect(card.textContent).toContain('Bulk');
    expect(card.querySelector('.badge.danger')?.textContent).toContain('Wrong value');
  });

  it('narrows the summary by tool and the feedback by tool and category', () => {
    select('tool', 'projects.request-review');

    expect(element().querySelectorAll('.summary-table tbody tr').length).toBe(1);
    expect(admin.getAgentFeedbackSummary).toHaveBeenCalledTimes(1);
    expect(admin.getAgentFeedback.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({
      toolId: 'projects.request-review',
      category: undefined,
      page: 1
    }));

    select('category', 'not-now');

    expect(admin.getAgentFeedback.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({
      toolId: 'projects.request-review',
      category: 'not-now'
    }));
  });

  it('reloads both parts for another period', () => {
    select('period', '7');

    const [, range] = admin.getAgentFeedbackSummary.calls.mostRecent().args;
    expect(admin.getAgentFeedbackSummary).toHaveBeenCalledTimes(2);
    expect(Date.parse(range!.to!) - Date.parse(range!.from!)).toBe(7 * 24 * 60 * 60 * 1000);
    expect(admin.getAgentFeedback.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ from: range!.from, to: range!.to }));
  });

  it('exports the filtered feedback as a JSON Lines file', async () => {
    admin.exportAgentFeedback.and.resolveTo(new Blob(['{}\n'], { type: 'application/x-ndjson' }));
    const click = spyOn(HTMLAnchorElement.prototype, 'click');
    select('category', 'wrong-value');

    exportButton().click();
    await flush();

    expect(admin.exportAgentFeedback).toHaveBeenCalledOnceWith('intake', jasmine.objectContaining({ category: 'wrong-value', toolId: undefined }));
    expect(click).toHaveBeenCalledTimes(1);
    expect((click.calls.mostRecent().object as HTMLAnchorElement).download).toBe('agent-feedback-intake.jsonl');
  });

  it('disables the export when there is nothing to export and explains an empty list', () => {
    admin.getAgentFeedback.and.returnValue(of({ items: [], total: 0 }));
    select('category', 'other');

    expect(exportButton().disabled).toBeTrue();
    expect(element().textContent).toContain('No feedback matches these filters.');
  });

  it('shows a localized error with a retry instead of the summary', async () => {
    admin.getAgentFeedbackSummary.and.returnValue(throwError(() => new NhAssistantApiError(403, 'assistant-forbidden', 'nh-assistant.errors.assistant-forbidden')));
    fixture.componentInstance.load();
    fixture.detectChanges();

    const alert = element().querySelector('.subsection [role="alert"]') as HTMLElement;
    expect(alert).not.toBeNull();
    expect(alert.textContent).not.toContain('assistant-forbidden');
    expect(alert.querySelector('button')?.textContent).toContain('Try again');
  });
});
