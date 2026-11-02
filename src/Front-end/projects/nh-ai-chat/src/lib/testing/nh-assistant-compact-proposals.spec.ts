import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideTranslateService } from '@ngx-translate/core';
import { NEVER, Subject, of } from 'rxjs';
import {
  NhAssistantAgentBoardComponent,
  NhAssistantAgentInboxComponent,
  NhAssistantAgentProposalsComponent,
  NhAssistantBoardInboxRequest,
  NhAssistantProposalDensity
} from '../../../agent-runs/src/public-api';
import { AgentBoard, AgentInbox, AgentInboxFilter, AgentProposal, AgentProposalDecision } from '../models/assistant-agent-run.models';
import { LiveAgentWorkChanged } from '../models/assistant-live.models';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantAgentRunApiService } from '../services/nh-assistant-agent-run-api.service';
import { NhAssistantLiveService, NhAssistantLiveState } from '../services/nh-assistant-live.service';

/**
 * The proposals list for many proposals: a selection that follows the list (FB84), so "Approve
 * selected" only sends what is shown, and the compact density (FB79): one row per proposal that
 * expands to its card, one tab stop with the arrow keys, and letters that act only while the focus is
 * in a row, never in a field; and the way from a board's count of open proposals to the host's inbox
 * (FB81).
 */
function proposal(id: string, overrides: Partial<AgentProposal> = {}): AgentProposal {
  return {
    approvalId: id,
    proposalId: `proposal-${id}`,
    proposalHash: `hash-${id}`,
    conversationId: `thread-${id}`,
    runId: 'run-1',
    agentId: 'planner',
    subjectType: 'project',
    subjectId: `project-${id}`,
    subjectDisplayName: `Project ${id}`,
    subjectTypeName: 'Project',
    toolId: 'projects.complete',
    summary: 'Approval is required before this tool can run.',
    presentation: null,
    argumentsPreview: '{}',
    targets: [],
    status: 'pending',
    mode: 'deferred',
    policyId: null,
    grantSource: 'agent',
    createdAt: '2026-10-20T10:00:00Z',
    expiresAt: '2026-10-23T10:00:00Z',
    initiatorActorId: null,
    decidedByActorId: null,
    decidedAt: null,
    executionResultCode: null,
    executedAt: null,
    ...overrides
  };
}

/** A connected hub whose agent-work updates the test sends. */
class FakeLiveService {
  readonly agentWork = new Subject<LiveAgentWorkChanged>();
  readonly changed$ = NEVER;
  readonly read$ = NEVER;
  readonly removed$ = NEVER;
  readonly events$ = NEVER;
  readonly agentWork$ = this.agentWork.asObservable();
  readonly resync$ = NEVER;
  readonly state = signal<NhAssistantLiveState>('connected').asReadonly();

  connect(): Promise<void> {
    return Promise.resolve();
  }

  stop(): Promise<void> {
    return Promise.resolve();
  }
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentInboxComponent],
  template: `<nh-assistant-agent-inbox [markNew]="false" [recordVisit]="false" [itemsPerPage]="2" [density]="density()" />`
})
class InboxHostComponent {
  readonly density = signal<NhAssistantProposalDensity>('comfortable');
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentProposalsComponent],
  template: `
    <div class="page" [style.width.px]="width()">
      <nh-assistant-agent-proposals
        [proposals]="proposals()"
        [canDecide]="true"
        [selectable]="true"
        [holdClosedCards]="true"
        [newIds]="newIds()"
        [density]="density()" />
    </div>
  `
})
class ProposalsHostComponent {
  readonly proposals = signal<AgentProposal[]>([]);
  readonly newIds = signal<ReadonlySet<string> | null>(null);
  readonly density = signal<NhAssistantProposalDensity>('comfortable');
  readonly width = signal(900);
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentBoardComponent],
  template: `<nh-assistant-agent-board group="regional-review" [divisionId]="divisionId" [compact]="compact()" (openInbox)="requests.push($event)" />`
})
class RoutingBoardHostComponent {
  readonly divisionId = '8d5c6f7e-1a2b-4c3d-9e8f-0a1b2c3d4e5f';
  readonly compact = signal(false);
  readonly requests: NhAssistantBoardInboxRequest[] = [];
}

@Component({
  standalone: true,
  imports: [NhAssistantAgentBoardComponent],
  template: `<nh-assistant-agent-board group="regional-review" [divisionId]="divisionId" [compact]="compact()" />`
})
class InlineBoardHostComponent {
  readonly divisionId = '8d5c6f7e-1a2b-4c3d-9e8f-0a1b2c3d4e5f';
  readonly compact = signal(false);
}

/** A board with one agent that works per division and has two open proposals in the board's scope. */
function board(divisionId: string): AgentBoard {
  return {
    id: 'regional-review',
    name: 'Regional review',
    description: null,
    scopeKey: `division:${divisionId.replaceAll('-', '')}`,
    divisionId,
    lastVisitedAt: '2026-10-20T12:00:00Z',
    agents: [{
      agentId: 'regional',
      displayNameKey: 'Regional review',
      descriptionKey: 'Reviews a division.',
      worksOn: { allObjects: false, application: true, perDivision: true, subjectTypes: [] },
      subjectType: 'nh-system',
      subjectId: `division-${divisionId.replaceAll('-', '')}`,
      subjectDisplayName: 'North',
      canStart: false,
      conversationId: null,
      latestRun: null,
      openProposals: 2,
      isNew: false
    }]
  };
}

function key(target: Element, name: string, init: KeyboardEventInit = {}): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(event);
  return event;
}

function row(element: HTMLElement, approvalId: string): HTMLButtonElement {
  return element.querySelector<HTMLButtonElement>(`[data-proposal-row="${approvalId}"]`)!;
}

function text(element: Element | null | undefined): string {
  return element?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function button(fixture: ComponentFixture<unknown>, key: string): HTMLButtonElement {
  const match = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'))
    .find(item => item.textContent?.includes(key));
  if (!match) {
    throw new Error(`No button "${key}".`);
  }
  return match;
}

function checkbox(element: HTMLElement, approvalId: string): HTMLInputElement {
  return element.querySelector<HTMLInputElement>(`[data-approval-id="${approvalId}"] input[type="checkbox"]`)!;
}

function cardIds(element: HTMLElement): string[] {
  return Array.from(element.querySelectorAll<HTMLElement>('li[data-approval-id]')).map(card => card.dataset['approvalId'] ?? '');
}

describe('the proposals list for many proposals', () => {
  let api: jasmine.SpyObj<NhAssistantAgentRunApiService>;

  beforeEach(() => {
    api = jasmine.createSpyObj<NhAssistantAgentRunApiService>('NhAssistantAgentRunApiService', [
      'getInbox',
      'visitInbox',
      'decide',
      'decideMany',
      'lookupProposals',
      'getBoard',
      'visitBoard'
    ]);
    api.visitBoard.and.returnValue(of({ groupId: 'regional-review', scopeKey: 'division', visitedAt: '2026-10-21T12:00:00Z' }));
    api.getBoard.and.callFake((_group: string, divisionId?: string | null) => of(board(divisionId!)));
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token', translations: 'host' }),
        { provide: NhAssistantAgentRunApiService, useValue: api },
        { provide: NhAssistantLiveService, useValue: new FakeLiveService() }
      ]
    });
  });

  describe('a selection that follows the list', () => {
    it('keeps a selection through a refresh of the same page and drops it on another page, so only shown proposals are approved', () => {
      const pages: Record<number, AgentInbox> = {
        1: { items: [proposal('a'), proposal('b')], total: 4 },
        2: { items: [proposal('c'), proposal('d')], total: 4 }
      };
      api.getInbox.and.callFake((filter: AgentInboxFilter = {}) => of(pages[filter.page ?? 1]));
      api.decideMany.and.returnValue(of({ results: [] }));
      const fixture = TestBed.createComponent(InboxHostComponent);
      fixture.detectChanges();
      const element = fixture.nativeElement as HTMLElement;
      const list = fixture.debugElement.query(By.directive(NhAssistantAgentProposalsComponent))
        .componentInstance as NhAssistantAgentProposalsComponent;

      checkbox(element, 'a').click();
      fixture.detectChanges();

      // A refresh of the same page keeps what the person is working on.
      button(fixture, 'agent-runs.refresh').click();
      fixture.detectChanges();
      expect(api.getInbox).toHaveBeenCalledTimes(2);
      expect(checkbox(element, 'a').checked).toBeTrue();
      expect([...list.selected()]).toEqual(['a']);

      // Another page shows other proposals: nothing of page 1 stays selected.
      button(fixture, 'inbox.next').click();
      fixture.detectChanges();
      expect(cardIds(element)).toEqual(['c', 'd']);
      expect(list.selected().size).toBe(0);
      expect(button(fixture, 'inbox.approve-selected').disabled).toBeTrue();

      checkbox(element, 'c').click();
      fixture.detectChanges();
      button(fixture, 'inbox.approve-selected').click();
      fixture.detectChanges();

      expect(api.decideMany).toHaveBeenCalledOnceWith(['c'], 'approve');
    });

    it('drops a selected proposal the host no longer shows, also when it comes back later', () => {
      api.decideMany.and.returnValue(of({ results: [] }));
      const fixture = TestBed.createComponent(ProposalsHostComponent);
      fixture.componentInstance.proposals.set([proposal('a'), proposal('b')]);
      fixture.detectChanges();
      const element = fixture.nativeElement as HTMLElement;
      const list = fixture.debugElement.query(By.directive(NhAssistantAgentProposalsComponent))
        .componentInstance as NhAssistantAgentProposalsComponent;

      checkbox(element, 'a').click();
      checkbox(element, 'b').click();
      fixture.detectChanges();

      // A colleague decided "a": the list no longer shows it.
      fixture.componentInstance.proposals.set([proposal('b'), proposal('c')]);
      fixture.detectChanges();
      expect([...list.selected()]).toEqual(['b']);

      fixture.componentInstance.proposals.set([proposal('a'), proposal('b'), proposal('c')]);
      fixture.detectChanges();
      expect(checkbox(element, 'a').checked).toBeFalse();

      button(fixture, 'inbox.approve-selected').click();
      expect(api.decideMany).toHaveBeenCalledOnceWith(['b'], 'approve');
    });
  });

  describe('the compact list', () => {
    let fixture: ComponentFixture<ProposalsHostComponent>;
    let element: HTMLElement;

    function render(proposals: AgentProposal[]): void {
      fixture = TestBed.createComponent(ProposalsHostComponent);
      fixture.componentInstance.density.set('compact');
      fixture.componentInstance.proposals.set(proposals);
      fixture.detectChanges();
      element = fixture.nativeElement as HTMLElement;
    }

    afterEach(() => fixture?.destroy());

    it('keeps the cards as the default', () => {
      fixture = TestBed.createComponent(ProposalsHostComponent);
      fixture.componentInstance.proposals.set([proposal('a')]);
      fixture.detectChanges();
      element = fixture.nativeElement as HTMLElement;

      expect(element.querySelector('[data-proposal-row]')).toBeNull();
      expect(element.querySelector('li.card .card-header')).not.toBeNull();
      expect(element.querySelector('[data-proposal-keys-toggle]')).toBeNull();
      // Letters do nothing in the comfortable list.
      key(element.querySelector('li.card')!, 'a');
      expect(api.decide).not.toHaveBeenCalled();
    });

    it('shows one row per proposal with its object, action, summary, agent and expiry, and keeps its markers', () => {
      render([
        proposal('a', { presentation: { toolDisplayName: 'Complete the project', summary: 'Complete Project a: all tasks are done.', fields: [], notice: null } }),
        proposal('b', { status: 'stale', staleKind: 'already-done', grantSource: 'none' }),
        proposal('c', { consultationId: 'consultation-1', consultationRoute: 'asker-decides', initiatorActorId: 'user-7', grantSource: 'delegated' })
      ]);
      fixture.componentInstance.newIds.set(new Set(['a']));
      fixture.detectChanges();

      const first = row(element, 'a');
      expect(text(first.querySelector('[data-row-subject]'))).toBe('Project a');
      expect(text(first.querySelector('[data-row-tool]'))).toBe('Complete the project');
      expect(text(first.querySelector('[data-row-summary]'))).toBe('Complete Project a: all tasks are done.');
      expect(text(first.querySelector('[data-row-agent]'))).toBe('planner');
      expect(text(first.querySelector('[data-row-expires]'))).toContain('proposal.expires');
      expect(first.querySelector('[data-proposal-new]')).not.toBeNull();
      expect(text(first.querySelector('[data-row-rights]'))).toContain('grant-source.agent');

      // A stale proposal keeps its status instead of an expiry.
      const stale = row(element, 'b');
      expect(text(stale.querySelector('[data-row-status]'))).toContain('stale-kind.already-done');
      expect(stale.querySelector('[data-row-expires]')).toBeNull();
      expect(stale.querySelector('[data-row-rights]')).toBeNull();

      // A consultation's proposal that the person who asked decides waits for them.
      const consulted = row(element, 'c');
      expect(consulted.querySelector('[data-row-consultation]')).not.toBeNull();
      expect(text(consulted.querySelector('[data-row-waiting]'))).toContain('compact.waiting');
      expect(text(consulted.querySelector('[data-row-waiting]'))).toContain('approval.waiting-for-asker');
      expect(text(consulted.querySelector('[data-row-rights]'))).toContain('grant-source.delegated');
      // Nobody selects or decides it here.
      expect(element.querySelector('[data-approval-id="c"] [data-row-select]')).toBeNull();

      // No cards until a row is expanded.
      expect(element.querySelector('.card-header')).toBeNull();
      expect(element.querySelectorAll('[data-row-select]').length).toBe(1);
    });

    it('expands a row in place to its card, which decides as the comfortable list does', () => {
      api.decide.and.returnValue(of({ ...proposal('a'), status: 'approved' }));
      render([proposal('a'), proposal('b')]);

      const first = row(element, 'a');
      expect(first.tagName).toBe('BUTTON');
      expect(first.getAttribute('aria-expanded')).toBe('false');
      first.click();
      fixture.detectChanges();

      expect(first.getAttribute('aria-expanded')).toBe('true');
      const details = element.querySelector<HTMLElement>(`#${first.getAttribute('aria-controls')}`)!;
      expect(details).not.toBeNull();
      expect(details.querySelector('.card-header')).not.toBeNull();
      // The row already holds the checkbox; the card does not repeat it.
      expect(details.querySelector('input[type="checkbox"]')).toBeNull();
      expect(element.querySelector('[data-approval-id="b"] .card-header')).toBeNull();

      Array.from(details.querySelectorAll('button')).find(item => item.textContent?.includes('proposal.approve'))!.click();
      expect(api.decide).toHaveBeenCalledOnceWith(jasmine.objectContaining({ approvalId: 'a' }), 'approve');

      first.click();
      fixture.detectChanges();
      expect(first.getAttribute('aria-expanded')).toBe('false');
      expect(element.querySelector('[data-row-details]')).toBeNull();
    });

    it('moves one tab stop over the rows with the arrow keys, Home, End, j and k, and selects with Space and x', () => {
      render([proposal('a'), proposal('b'), proposal('c')]);
      const tabStops = () => Array.from(element.querySelectorAll<HTMLElement>('[data-proposal-row]'))
        .filter(item => item.tabIndex === 0)
        .map(item => item.dataset['proposalRow']);
      expect(tabStops()).toEqual(['a']);
      // The checkboxes stay out of the tab order; the rows select by key.
      expect(Array.from(element.querySelectorAll<HTMLInputElement>('[data-row-select]')).every(item => item.tabIndex === -1)).toBeTrue();

      row(element, 'a').focus();
      key(row(element, 'a'), 'ArrowDown');
      fixture.detectChanges();
      expect(document.activeElement).toBe(row(element, 'b'));
      expect(tabStops()).toEqual(['b']);

      key(row(element, 'b'), 'End');
      expect(document.activeElement).toBe(row(element, 'c'));
      key(row(element, 'c'), 'ArrowDown');
      expect(document.activeElement).toBe(row(element, 'c'));
      key(row(element, 'c'), 'Home');
      expect(document.activeElement).toBe(row(element, 'a'));
      key(row(element, 'a'), 'j');
      expect(document.activeElement).toBe(row(element, 'b'));
      key(row(element, 'b'), 'k');
      expect(document.activeElement).toBe(row(element, 'a'));
      key(row(element, 'a'), 'ArrowUp');
      expect(document.activeElement).toBe(row(element, 'a'));

      const space = key(row(element, 'a'), ' ');
      fixture.detectChanges();
      expect(space.defaultPrevented).toBeTrue();
      expect((element.querySelector('[data-approval-id="a"] [data-row-select]') as HTMLInputElement).checked).toBeTrue();
      expect(text(element.querySelector('[data-proposal-key-status]'))).toContain('compact.status.selected');
      // Space selected the row; it does not expand it as well.
      expect(row(element, 'a').getAttribute('aria-expanded')).toBe('false');

      key(row(element, 'a'), 'x');
      fixture.detectChanges();
      expect((element.querySelector('[data-approval-id="a"] [data-row-select]') as HTMLInputElement).checked).toBeFalse();
      expect(text(element.querySelector('[data-proposal-key-status]'))).toContain('compact.status.deselected');
    });

    it('decides only the focused row with a and r, never the selection, asks after r and gives the next row the focus', async () => {
      const [a, b, c] = [proposal('a'), proposal('b'), proposal('c')];
      api.decide.and.callFake((item: AgentProposal, decision: AgentProposalDecision) =>
        of({ ...item, status: decision === 'reject' ? 'rejected' : 'approved', canGiveFeedback: decision === 'reject' }));
      render([a, b, c]);

      // "c" is selected, but a and r decide the row with the focus.
      (element.querySelector('[data-approval-id="c"] [data-row-select]') as HTMLInputElement).click();
      fixture.detectChanges();
      row(element, 'a').focus();
      key(row(element, 'a'), 'r');
      fixture.detectChanges();

      expect(api.decide).toHaveBeenCalledOnceWith(a, 'reject');
      expect(api.decideMany).not.toHaveBeenCalled();
      // Rejecting stays one key; the question about what was wrong follows, as after a click.
      expect(element.querySelector('.feedback-prompt nh-assistant-proposal-feedback')).not.toBeNull();
      expect(text(element.querySelector('[data-proposal-key-status]'))).toContain('compact.status.rejected-feedback');

      // The host reloads without the rejected proposal: the row that takes its place gets the focus.
      fixture.componentInstance.proposals.set([b, c]);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      expect(document.activeElement).toBe(row(element, 'b'));

      key(row(element, 'b'), 'a');
      fixture.detectChanges();
      expect(api.decide).toHaveBeenCalledWith(b, 'approve');
      expect(api.decide).toHaveBeenCalledTimes(2);
      expect(api.decideMany).not.toHaveBeenCalled();
      expect([...(fixture.debugElement.query(By.directive(NhAssistantAgentProposalsComponent)).componentInstance as NhAssistantAgentProposalsComponent).selected()])
        .toEqual(['c']);

      // A held key decides nothing more.
      key(row(element, 'c'), 'a', { repeat: true });
      expect(api.decide).toHaveBeenCalledTimes(2);
    });

    it('lets the letters act only in a row, never in a field or the feedback form, and only without a modifier', () => {
      const closed = proposal('closed', { status: 'rejected', canGiveFeedback: true });
      api.decide.and.returnValue(of({ ...proposal('a'), status: 'rejected', canGiveFeedback: true }));
      render([proposal('a'), closed]);

      // The question after a rejection is a form: typing there never decides.
      row(element, 'a').focus();
      key(row(element, 'a'), 'r');
      fixture.detectChanges();
      expect(api.decide).toHaveBeenCalledTimes(1);
      const form = element.querySelector('.feedback-prompt nh-assistant-proposal-feedback') as HTMLElement;
      for (const field of Array.from(form.querySelectorAll<HTMLElement>('input, textarea, button'))) {
        key(field, 'a');
        key(field, 'r');
      }
      key(form, 'a');
      expect(api.decide).toHaveBeenCalledTimes(1);

      // Nor in the row's checkbox, the toolbar, or with a modifier.
      key(element.querySelector('[data-approval-id="a"] [data-row-select]')!, 'a');
      key(element.querySelector('.toolbar input[type="checkbox"]')!, 'a');
      key(element.querySelector('[data-proposal-keys-toggle]')!, 'a');
      key(row(element, 'a'), 'a', { ctrlKey: true });
      key(row(element, 'a'), 'a', { altKey: true });
      key(row(element, 'a'), 'a', { metaKey: true });
      expect(api.decide).toHaveBeenCalledTimes(1);

      // A proposal that no longer waits is not decided by key; the list says so.
      key(row(element, 'closed'), 'a');
      fixture.detectChanges();
      expect(api.decide).toHaveBeenCalledTimes(1);
      expect(text(element.querySelector('[data-proposal-key-status]'))).toContain('compact.status.not-decidable');
    });

    it('lists the keys with ? and returns the focus to the row with Escape', async () => {
      render([proposal('a'), proposal('b')]);
      const toggle = element.querySelector<HTMLButtonElement>('[data-proposal-keys-toggle]')!;
      expect(toggle.getAttribute('aria-expanded')).toBe('false');

      row(element, 'b').focus();
      key(row(element, 'b'), '?', { shiftKey: true });
      fixture.detectChanges();
      await fixture.whenStable();

      const keys = element.querySelector<HTMLElement>('[data-proposal-keys]')!;
      expect(keys).not.toBeNull();
      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(toggle.getAttribute('aria-controls')).toBe(keys.id);
      expect(document.activeElement).toBe(keys);
      expect(Array.from(keys.querySelectorAll('dt')).map(item => text(item))).toEqual([
        'nh-assistant.agent-runs.compact.keys.move.keys',
        'nh-assistant.agent-runs.compact.keys.edges.keys',
        'nh-assistant.agent-runs.compact.keys.expand.keys',
        'nh-assistant.agent-runs.compact.keys.select.keys',
        'nh-assistant.agent-runs.compact.keys.approve.keys',
        'nh-assistant.agent-runs.compact.keys.reject.keys',
        'nh-assistant.agent-runs.compact.keys.help.keys'
      ]);

      key(keys, 'Escape');
      fixture.detectChanges();
      await fixture.whenStable();
      expect(element.querySelector('[data-proposal-keys]')).toBeNull();
      expect(document.activeElement).toBe(row(element, 'b'));
    });

    it('fits a 375px page without scrolling sideways', () => {
      render([
        proposal('a', { subjectDisplayName: 'A project with a rather long name that wraps', presentation: { toolDisplayName: 'Complete the project', summary: 'A summary that is far longer than one line of a phone can show at once.', fields: [], notice: null } }),
        proposal('b')
      ]);
      fixture.componentInstance.width.set(375);
      fixture.detectChanges();

      const page = element.querySelector('.page') as HTMLElement;
      expect(page.scrollWidth).toBeLessThanOrEqual(page.clientWidth + 1);
      for (const item of Array.from(element.querySelectorAll<HTMLElement>('li[data-approval-id]'))) {
        expect(item.getBoundingClientRect().right).toBeLessThanOrEqual(page.getBoundingClientRect().right + 0.5);
      }
    });

    it('is passed through by the inbox', () => {
      api.getInbox.and.returnValue(of({ items: [proposal('a'), proposal('b')], total: 2 }));
      const inbox = TestBed.createComponent(InboxHostComponent);
      inbox.componentInstance.density.set('compact');
      inbox.detectChanges();

      const rows = Array.from((inbox.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('[data-proposal-row]'));
      expect(rows.map(item => item.dataset['proposalRow'])).toEqual(['a', 'b']);
      inbox.destroy();
    });
  });

  describe('the way from a board to the inbox', () => {
    it('emits the agent and division from the count of open proposals to a host that listens, in either density', () => {
      const fixture = TestBed.createComponent(RoutingBoardHostComponent);
      fixture.detectChanges();
      const element = fixture.nativeElement as HTMLElement;

      for (const compact of [false, true]) {
        fixture.componentInstance.compact.set(compact);
        fixture.detectChanges();
        const count = element.querySelector<HTMLButtonElement>('[data-board-open-inbox]')!;
        expect(count).not.toBeNull();
        expect(count.tagName).toBe('BUTTON');
        expect(text(count)).toContain('object-agents.proposals');
        expect(text(count)).toContain('board.open-inbox');
        // The host's inbox shows the proposals; the tile has no inline inbox of its own.
        expect(element.querySelector('[data-board-toggle-proposals]')).toBeNull();
        count.click();
        fixture.detectChanges();
      }

      expect(fixture.componentInstance.requests).toEqual([
        { agentId: 'regional', divisionId: '8d5c6f7e-1a2b-4c3d-9e8f-0a1b2c3d4e5f' },
        { agentId: 'regional', divisionId: '8d5c6f7e-1a2b-4c3d-9e8f-0a1b2c3d4e5f' }
      ]);
      expect(element.querySelector('nh-assistant-agent-inbox')).toBeNull();
      expect(api.getInbox).not.toHaveBeenCalled();
      fixture.destroy();
    });

    it('opens the own inbox of a tile from the count of a compact tile while nobody listens, and keeps the comfortable tile as it was', () => {
      api.getInbox.and.returnValue(of({ items: [proposal('a', { agentId: 'regional' })], total: 1 }));
      const fixture = TestBed.createComponent(InlineBoardHostComponent);
      fixture.detectChanges();
      const element = fixture.nativeElement as HTMLElement;

      // Comfortable: the count is a badge and "Show the proposals" opens the inline inbox, as before.
      expect(element.querySelector('span[data-open-proposals]')).not.toBeNull();
      expect(element.querySelector('[data-board-open-inbox]')).toBeNull();
      expect(element.querySelector('.card-actions [data-board-toggle-proposals]')).not.toBeNull();

      fixture.componentInstance.compact.set(true);
      fixture.detectChanges();
      const count = element.querySelector<HTMLButtonElement>('button[data-open-proposals]')!;
      expect(count.hasAttribute('data-board-toggle-proposals')).toBeTrue();
      expect(element.querySelector('.card-actions [data-board-toggle-proposals]')).toBeNull();
      expect(count.getAttribute('aria-expanded')).toBe('false');

      count.click();
      fixture.detectChanges();
      expect(count.getAttribute('aria-expanded')).toBe('true');
      const panel = element.querySelector<HTMLElement>(`#${count.getAttribute('aria-controls')}`)!;
      expect(panel.querySelector('nh-assistant-agent-inbox')).not.toBeNull();
      expect(api.getInbox).toHaveBeenCalledOnceWith(jasmine.objectContaining({
        agentId: 'regional',
        divisionId: '8d5c6f7e-1a2b-4c3d-9e8f-0a1b2c3d4e5f'
      }));
      fixture.destroy();
    });
  });
});
