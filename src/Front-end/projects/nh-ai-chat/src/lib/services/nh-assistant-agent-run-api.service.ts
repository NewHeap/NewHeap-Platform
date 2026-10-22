import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentArtifact,
  AgentBoard,
  AgentBoardList,
  AgentBoardVisit,
  AgentConsultationList,
  AgentDecisionOutcome,
  AgentExecutionResolution,
  AgentFeedbackOutcome,
  AgentInbox,
  AgentInboxFilter,
  AgentInboxVisit,
  AgentObjectAgents,
  AgentProposal,
  AgentProposalDecision,
  AgentProposalFeedback,
  AgentProposalFeedbackCategory,
  AgentProposalLookup,
  AgentRunFilter,
  AgentRunHistory,
  AgentRunList,
  AgentRunResult,
  AgentRunRetryMode,
  AgentSubjectSearch,
  AgentSubjectSummaryList,
  AgentSubjectWork,
  AgentSubjectWorkProposal,
  AgentThreadLookup,
  AgentThreadMemory,
  AgentTriggerReceipt,
  StartAgentRequest
} from '../models/assistant-agent-run.models';
import { NhAssistantTransport } from './nh-assistant-transport';

/**
 * Client for the agent-run endpoints: the agent thread about a subject, its runs, proposals,
 * working memory and artifacts, and the approval inbox. Access follows the caller's role on the
 * subject, which the server checks on every request; failures error with `NhAssistantApiError`.
 */
@Injectable()
export class NhAssistantAgentRunApiService {
  private readonly transport = inject(NhAssistantTransport);

  /** The agent's thread about the subject, the caller's role and whether the caller may start the agent. */
  getThread(agentId: string, subjectType: string, subjectId: string): Observable<AgentThreadLookup> {
    const query = new URLSearchParams({ agentId, subjectType, subjectId });
    return this.transport.json<AgentThreadLookup>('GET', `threads?${query.toString()}`);
  }

  /**
   * The agent's thread about the subject, created without starting a run when it is missing, so
   * the caller can open the conversation and type there. Creating a thread needs the participant
   * role; readers may open an existing one. Errors with `assistant-subject-access-denied` otherwise.
   */
  openThread(agentId: string, subjectType: string, subjectId: string): Observable<AgentThreadLookup> {
    return this.transport.json<AgentThreadLookup>('POST', 'threads/open', { agentId, subjectType, subjectId });
  }

  /**
   * Runs the agent now on the subject with the caller as initiator. On an object it needs the
   * participant role; on the whole application (`nh-system`) the agent's run policy, by default the
   * administration policy. The receipt says what became of the start (`AgentTriggerOutcome`), for
   * example `waiting` while the agent is paused. A refusal errors with `NhAssistantApiError`:
   * `assistant-agent-does-not-work-on`, `assistant-agent-stopped`, `assistant-trigger-rate-limited`
   * and the other conflicts (409), `assistant-subject-access-denied` or `assistant-agent-forbidden`
   * (403), `assistant-agent-not-found` or `assistant-subject-not-found` (404),
   * `assistant-subject-invalid` (400) and `assistant-trigger-rolled-back` (503).
   */
  startAgent(request: StartAgentRequest): Observable<AgentTriggerReceipt> {
    return this.transport.json<AgentTriggerReceipt>('POST', 'threads/start', request);
  }

  /**
   * All agent work on one subject: the caller's role, every agent with a thread on it plus
   * `agentId`, and the open proposals of all of them with the caller's live decision right. Errors
   * with `assistant-subject-not-found` when the caller has no role on the subject.
   */
  getSubjectWork(subjectType: string, subjectId: string, agentId?: string): Observable<AgentSubjectWork> {
    const path = `subjects/${encodeURIComponent(subjectType)}/${encodeURIComponent(subjectId)}/work`;
    const query = agentId ? `?${new URLSearchParams({ agentId }).toString()}` : '';
    return this.transport.json<AgentSubjectWork>('GET', path + query);
  }

  /**
   * The agents that work on the subject and that the caller may use, with whether the caller may run
   * each now, its newest run with the beginning of its result and its open proposals. The subject is
   * an object or the whole application (`nh-system`, `global` or `division-…`). Errors with
   * `assistant-subject-not-found` when the caller has no role on the subject.
   */
  getObjectAgents(subjectType: string, subjectId: string): Observable<AgentObjectAgents> {
    return this.transport.json<AgentObjectAgents>(
      'GET',
      `subjects/${encodeURIComponent(subjectType)}/${encodeURIComponent(subjectId)}/agents`
    );
  }

  /**
   * Counts the agent work on at most `NH_ASSISTANT_MAX_SUBJECT_SUMMARY` subjects of one type in one
   * request, for badges in a list; one item per id in request order. `NhAssistantSubjectSummaryService`
   * batches and caches these requests for many badges.
   */
  summarizeSubjects(subjectType: string, subjectIds: readonly string[]): Observable<AgentSubjectSummaryList> {
    return this.transport.json<AgentSubjectSummaryList>('POST', `subjects/${encodeURIComponent(subjectType)}/summary`, {
      subjectIds
    });
  }

  /**
   * Finds subjects of one type by name, at most `limit` (default 20, at most 50), among those the
   * caller may read; `query` has 2 to 100 characters. Errors with `assistant-subject-search-unavailable`
   * for a type the application does not search (people then give the subject's id), with
   * `assistant-subject-type-unknown` for an unknown type and with `assistant-validation` for a
   * query of the wrong length.
   */
  searchSubjects(subjectType: string, query: string, limit?: number): Observable<AgentSubjectSearch> {
    const parameters = new URLSearchParams({ query });
    if (limit !== undefined) {
      parameters.set('limit', String(limit));
    }
    return this.transport.json<AgentSubjectSearch>(
      'GET',
      `subjects/${encodeURIComponent(subjectType)}/search?${parameters.toString()}`
    );
  }

  getRuns(conversationId: string): Observable<AgentRunList> {
    return this.transport.json<AgentRunList>('GET', `threads/${encodeURIComponent(conversationId)}/runs`);
  }

  /** Who consulted the thread's agent through another assistant, newest first. */
  getConsultations(conversationId: string, page = 1, itemsPerPage = 20): Observable<AgentConsultationList> {
    const query = new URLSearchParams({ page: String(page), itemsPerPage: String(itemsPerPage) });
    return this.transport.json<AgentConsultationList>(
      'GET',
      `threads/${encodeURIComponent(conversationId)}/consultations?${query.toString()}`
    );
  }

  getProposals(conversationId: string): Observable<AgentProposal[]> {
    return this.transport.json<AgentProposal[]>('GET', `threads/${encodeURIComponent(conversationId)}/proposals`);
  }

  /**
   * One proposal of an agent thread in any status, with the caller's live right to decide it, for
   * example the one an `AgentWorkChanged` update names. Fails with `assistant-approval-not-found`
   * for an unknown proposal and for one the caller may not see.
   */
  getProposal(approvalId: string): Observable<AgentSubjectWorkProposal> {
    return this.transport.json<AgentSubjectWorkProposal>('GET', `approvals/${encodeURIComponent(approvalId)}`);
  }

  /**
   * The proposals among at most `NH_ASSISTANT_MAX_PROPOSAL_LOOKUP` ids that the caller may see, in
   * the order of the ids, each with the caller's live right to decide it. The others are left out.
   */
  lookupProposals(approvalIds: readonly string[]): Observable<AgentProposalLookup> {
    return this.transport.json<AgentProposalLookup>('POST', 'approvals/lookup', { approvalIds });
  }

  getMemory(conversationId: string): Observable<AgentThreadMemory> {
    return this.transport.json<AgentThreadMemory>('GET', `threads/${encodeURIComponent(conversationId)}/memory`);
  }

  getArtifacts(conversationId: string): Observable<AgentArtifact[]> {
    return this.transport.json<AgentArtifact[]>('GET', `threads/${encodeURIComponent(conversationId)}/artifacts`);
  }

  /**
   * Downloads one artifact version with the host's credentials. Rejects with `NhAssistantApiError`,
   * for example `assistant-artifact-not-found` or `assistant-network`.
   */
  downloadArtifact(conversationId: string, artifactId: string): Promise<Blob> {
    const path = `threads/${encodeURIComponent(conversationId)}/artifacts/${encodeURIComponent(artifactId)}/content`;
    return this.transport.download(path, '*/*');
  }

  /**
   * The agent runs the caller may see, newest first: runs on objects the caller reads and runs for
   * the whole application whose agent's view policy the caller passes. Errors with
   * `assistant-run-filter-invalid` for an invalid filter, with the refused parts in `fieldErrors`
   * (`page`, `itemsPerPage`, `agentId`, `subjectType`, `subjectId`, `status`, `to`).
   */
  getRunHistory(filter: AgentRunFilter = {}): Observable<AgentRunHistory> {
    return this.transport.json<AgentRunHistory>('GET', withQuery('runs', filter));
  }

  /**
   * The boards of the groups of agents the caller sees at least one agent of, ordered by name: an
   * agent that works on the whole application by its view policy, another by its required policy.
   */
  getBoards(): Observable<AgentBoardList> {
    return this.transport.json<AgentBoardList>('GET', 'boards');
  }

  /**
   * The board of one group: per agent what it works on, its latest result for the whole application,
   * its open proposals, whether the caller may run it now and whether something is new since the
   * caller's last visit. `divisionId` opens the board of one division the caller may work in. Fails
   * with `assistant-board-not-found` for an unknown group or one the caller sees no agent of, and with
   * `assistant-board-scope-invalid` for a malformed division id.
   */
  getBoard(groupId: string, divisionId?: string | null): Observable<AgentBoard> {
    return this.transport.json<AgentBoard>('GET', withQuery(`boards/${encodeURIComponent(groupId)}`, { divisionId }));
  }

  /** Records that the caller looked at the board now, so it marks what is new after this visit. */
  visitBoard(groupId: string, divisionId?: string | null): Observable<AgentBoardVisit> {
    return this.transport.json<AgentBoardVisit>('POST', withQuery(`boards/${encodeURIComponent(groupId)}/visit`, { divisionId }));
  }

  /**
   * One run with its whole result, its proposals and its files. Fails with `assistant-run-not-found`
   * for an unknown run and for one the caller may not see.
   */
  getRunResult(runId: string): Observable<AgentRunResult> {
    return this.transport.json<AgentRunResult>('GET', `runs/${encodeURIComponent(runId)}`);
  }

  /**
   * Downloads one file a run saved, with the host's credentials. Rejects with `NhAssistantApiError`,
   * for example `assistant-artifact-not-found`, `assistant-run-not-found` or `assistant-network`.
   */
  downloadRunArtifact(runId: string, artifactId: string): Promise<Blob> {
    const path = `runs/${encodeURIComponent(runId)}/artifacts/${encodeURIComponent(artifactId)}/content`;
    return this.transport.download(path, '*/*');
  }

  /** Cancels a run at its next safe point and withdraws its open proposals; needs the participant role. */
  cancelRun(runId: string): Observable<void> {
    return this.transport.json<void>('POST', `runs/${encodeURIComponent(runId)}/cancel`);
  }

  /**
   * Resumes or restarts a failed or cancelled run; needs the approver role. A restart starts the agent
   * again like `startAgent`, with the same outcomes and refusals; a resume answers `queued`.
   */
  retryRun(runId: string, mode: AgentRunRetryMode): Observable<AgentTriggerReceipt> {
    return this.transport.json<AgentTriggerReceipt>('POST', `runs/${encodeURIComponent(runId)}/retry`, { mode });
  }

  /**
   * Pending proposals the caller may decide, sorted by expiry, or with `grouped` by parent and subject,
   * with what is new since the caller's last visit (`visitInbox`).
   */
  getInbox(filter: AgentInboxFilter = {}): Observable<AgentInbox> {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(filter)) {
      if (value !== undefined && value !== null && value !== '') {
        query.set(key, String(value));
      }
    }
    const suffix = query.toString();
    return this.transport.json<AgentInbox>('GET', suffix ? `approvals/inbox?${suffix}` : 'approvals/inbox');
  }

  /**
   * Records that the caller looked at the inbox now, so it marks the proposals made after this visit
   * as new. `divisionId` records the visit of the inbox of one division the caller may work in;
   * otherwise of the whole inbox. Fails with `assistant-subject-not-found` for a division the caller
   * may not work in.
   */
  visitInbox(divisionId?: string | null): Observable<AgentInboxVisit> {
    return this.transport.json<AgentInboxVisit>('POST', withQuery('approvals/inbox/visit', { divisionId }));
  }

  /**
   * Decides one proposal, bound to the proposal hash the person saw; the server checks the approval
   * policy and the tool's approve policy again. Accepts an inbox proposal or the approval part of an
   * agent thread.
   */
  decide(
    proposal: Pick<AgentProposal, 'approvalId' | 'proposalHash'>,
    decision: AgentProposalDecision,
    reason?: string
  ): Observable<AgentProposal> {
    return this.transport.json<AgentProposal>('POST', `approvals/${encodeURIComponent(proposal.approvalId)}/decisions`, {
      decision,
      expectedProposalHash: proposal.proposalHash,
      reason: reason?.trim() || null
    });
  }

  /** Decides several proposals; each one is checked and decided on its own. */
  decideMany(approvalIds: string[], decision: AgentProposalDecision, reason?: string): Observable<{ results: AgentDecisionOutcome[] }> {
    return this.transport.json<{ results: AgentDecisionOutcome[] }>('POST', 'approvals/decisions', {
      approvalIds,
      decision,
      reason: reason?.trim() || null
    });
  }

  /** Resolves an execution whose outcome is unknown. */
  resolve(approvalId: string, action: AgentExecutionResolution, reason?: string): Observable<AgentProposal> {
    return this.transport.json<AgentProposal>('POST', `approvals/${encodeURIComponent(approvalId)}/resolve`, {
      action,
      reason: reason?.trim() || null
    });
  }

  /**
   * Gives or changes the caller's feedback on a closed proposal (`canGiveFeedback`). Feedback is
   * optional and separate from the decision; an empty text is sent as `null`. Fails with
   * `assistant-feedback-not-accepted` while the proposal is pending or after it was approved.
   */
  giveFeedback(
    approvalId: string,
    category: AgentProposalFeedbackCategory,
    text?: string | null
  ): Observable<AgentProposalFeedback> {
    return this.transport.json<AgentProposalFeedback>('PUT', `approvals/${encodeURIComponent(approvalId)}/feedback`, {
      category,
      text: text?.trim() || null
    });
  }

  /**
   * Gives the same feedback on at most `NH_ASSISTANT_MAX_BULK_FEEDBACK` proposals that were
   * rejected together. Each proposal is checked on its own and stored as bulk feedback.
   */
  giveFeedbackMany(
    approvalIds: readonly string[],
    category: AgentProposalFeedbackCategory,
    text?: string | null
  ): Observable<{ results: AgentFeedbackOutcome[] }> {
    return this.transport.json<{ results: AgentFeedbackOutcome[] }>('POST', 'approvals/feedback', {
      approvalIds,
      category,
      text: text?.trim() || null
    });
  }
}

/** Appends the filter values that are set as query parameters. */
function withQuery(path: string, filter: object): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(filter)) {
    if (value !== undefined && value !== null && value !== '') {
      query.set(key, String(value));
    }
  }

  const suffix = query.toString();
  return suffix ? `${path}?${suffix}` : path;
}
