import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { NhAssistantAgentProposalsComponent, NhAssistantAgentThreadViewComponent } from '@newheap/platform-ai-chat/agent-runs';
import { NEVER, Subject, of } from 'rxjs';
import { AgentProposal, AgentThreadLookup } from '../models/assistant-agent-run.models';
import { LiveAgentWorkChanged } from '../models/assistant-live.models';
import { NhAssistantSubjectLinkBuilder } from '../nh-assistant.config';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantLiveService, NhAssistantLiveState } from '../services/nh-assistant-live.service';

@Component({ standalone: true, template: '' })
class BlankComponent {}

@Component({
  standalone: true,
  imports: [NhAssistantAgentProposalsComponent],
  template: `<nh-assistant-agent-proposals [proposals]="proposals()" />`
})
class ProposalsHostComponent {
  readonly proposals = signal<AgentProposal[]>([]);
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentThreadViewComponent],
  template: `<nh-assistant-agent-thread-view agentId="intake" subjectType="project" subjectId="p 1" />`
})
class ThreadHostComponent {}

class FakeLiveService {
  readonly agentWork$ = new Subject<LiveAgentWorkChanged>().asObservable();
  readonly resync$ = NEVER;
  readonly changed$ = NEVER;
  readonly read$ = NEVER;
  readonly removed$ = NEVER;
  readonly events$ = NEVER;
  readonly state = signal<NhAssistantLiveState>('connected').asReadonly();

  connect(): Promise<void> {
    return Promise.resolve();
  }

  stop(): Promise<void> {
    return Promise.resolve();
  }
}

function proposal(overrides: Partial<AgentProposal> = {}): AgentProposal {
  return {
    approvalId: 'approval-1',
    proposalId: 'proposal-1',
    proposalHash: 'hash-1',
    conversationId: 'conversation-1',
    runId: 'run-1',
    agentId: 'intake',
    subjectType: 'project',
    subjectId: 'p 1',
    subjectDisplayName: 'PRJ-1 — Pietje',
    subjectTypeName: 'Project',
    subjectLink: '/management/intake-reviews?project=p%201',
    toolId: 'projects.change-status',
    summary: 'Activate the project.',
    presentation: null,
    argumentsPreview: '{}',
    targets: [],
    status: 'pending',
    mode: 'deferred',
    policyId: 'project-intake',
    grantSource: 'agent',
    createdAt: '2026-10-13T08:00:00Z',
    expiresAt: '2026-10-16T08:00:00Z',
    initiatorActorId: null,
    decidedByActorId: null,
    decidedAt: null,
    executionResultCode: null,
    executedAt: null,
    ...overrides
  };
}

describe('subject names and links', () => {
  let api: jasmine.SpyObj<NhAssistantAgentRunApiService>;

  function setup(subjectLinks?: Record<string, NhAssistantSubjectLinkBuilder>): void {
    api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', [
      'getThread',
      'getProposals',
      'getRuns',
      'lookupProposals'
    ]);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '**', component: BlankComponent }]),
        provideTranslateService(),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host', subjectLinks }),
        { provide: NhAssistantAgentRunApiService, useValue: api },
        { provide: NhAssistantLiveService, useValue: new FakeLiveService() }
      ]
    });
  }

  function cardLink(fixture: ComponentFixture<unknown>): HTMLAnchorElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector('.card-header a');
  }

  function showProposal(overrides: Partial<AgentProposal> = {}): ComponentFixture<ProposalsHostComponent> {
    const fixture = TestBed.createComponent(ProposalsHostComponent);
    fixture.componentInstance.proposals.set([proposal(overrides)]);
    fixture.detectChanges();
    return fixture;
  }

  it('names the subject type and opens the server link to the subject through the router', async () => {
    setup();
    const fixture = showProposal();
    const header = (fixture.nativeElement as HTMLElement).querySelector('.card-header')!;
    expect(header.querySelector('.nh-assistant-subject-type')!.textContent).toContain('Project');

    const link = cardLink(fixture)!;
    expect(link.textContent).toContain('PRJ-1 — Pietje');
    expect(link.getAttribute('href')).toBe('/management/intake-reviews?project=p%201');
    const router = TestBed.inject(Router);
    const navigate = spyOn(router, 'navigateByUrl').and.resolveTo(true);
    // Records whether the link kept the click from the browser, then keeps the test page in place.
    const prevented: boolean[] = [];
    (fixture.nativeElement as HTMLElement).addEventListener('click', event => {
      prevented.push(event.defaultPrevented);
      event.preventDefault();
    });

    // A modified click keeps the browser's own behavior, such as a new tab.
    link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true }));
    expect(navigate).not.toHaveBeenCalled();

    link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
    expect(navigate).toHaveBeenCalledOnceWith('/management/intake-reviews?project=p%201');
    expect(prevented).toEqual([false, true]);
  });

  it('lets the host replace the link with its own route, keep it or show none', () => {
    setup({
      project: subject => ['/projects', subject.id],
      ticket: () => null,
      order: () => undefined
    });

    expect(cardLink(showProposal())!.getAttribute('href')).toBe('/projects/p%201');
    expect(cardLink(showProposal({ subjectType: 'ticket', subjectLink: '/tickets/p%201' }))).toBeNull();
    expect(cardLink(showProposal({ subjectType: 'order', subjectLink: '/orders/p%201' }))!.getAttribute('href')).toBe('/orders/p%201');
  });

  it('links outside the application only over https and shows plain names without a link or type name', () => {
    setup();

    expect(cardLink(showProposal({ subjectLink: 'https://crm.example.com/projects/p%201' }))!.getAttribute('href'))
      .toBe('https://crm.example.com/projects/p%201');
    for (const unsafe of ['javascript:alert(1)', '//evil.example.com/p', 'http://crm.example.com/p', 'projects/p']) {
      expect(cardLink(showProposal({ subjectLink: unsafe }))).withContext(unsafe).toBeNull();
    }

    const plain = showProposal({ subjectTypeName: null, subjectLink: null });
    const header = (plain.nativeElement as HTMLElement).querySelector('.card-header')!;
    expect(header.querySelector('.nh-assistant-subject-type')).toBeNull();
    expect(header.textContent).toContain('PRJ-1 — Pietje');
  });

  it('heads the thread view with the subject type and a link to the subject', () => {
    setup();
    const lookup: AgentThreadLookup = {
      canStart: false,
      role: 'reader',
      thread: {
        conversationId: 'conversation-1',
        agentId: 'intake',
        subjectType: 'project',
        subjectId: 'p 1',
        subjectDisplayName: 'PRJ-1 — Pietje',
        subjectTypeName: 'Project',
        subjectLink: '/management/intake-reviews?project=p%201',
        accountability: 'agent',
        accountableOwnerId: 'agent:intake',
        role: 'reader',
        activeRun: null,
        waitingInputs: 0
      }
    };
    api.getThread.and.returnValue(of(lookup));
    api.getProposals.and.returnValue(of([]));
    api.getRuns.and.returnValue(of({ items: [], total: 0 }));

    const fixture = TestBed.createComponent(ThreadHostComponent);
    fixture.detectChanges();

    const title = (fixture.nativeElement as HTMLElement).querySelector('#nh-assistant-agent-thread-title')!;
    expect(title.querySelector('.nh-assistant-subject-type')!.textContent).toContain('Project');
    // Screen readers read the type and the name as two words.
    expect(title.textContent!.replace(/\s+/g, ' ').trim()).toBe('Project PRJ-1 — Pietje');
    expect(title.querySelector('a')!.getAttribute('href')).toBe('/management/intake-reviews?project=p%201');
  });
});
