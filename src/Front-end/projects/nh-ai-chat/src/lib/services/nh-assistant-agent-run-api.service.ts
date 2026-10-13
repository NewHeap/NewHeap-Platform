import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentArtifact,
  AgentConsultationList,
  AgentDecisionOutcome,
  AgentExecutionResolution,
  AgentFeedbackOutcome,
  AgentInbox,
  AgentInboxFilter,
  AgentProposal,
  AgentProposalDecision,
  AgentProposalFeedback,
  AgentProposalFeedbackCategory,
  AgentRunList,
  AgentRunRetryMode,
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

  /** Starts the agent on the subject with the caller as initiator; needs the participant role. */
  startAgent(request: StartAgentRequest): Observable<AgentTriggerReceipt> {
    return this.transport.json<AgentTriggerReceipt>('POST', 'threads/start', request);
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

  getMemory(conversationId: string): Observable<AgentThreadMemory> {
    return this.transport.json<AgentThreadMemory>('GET', `threads/${encodeURIComponent(conversationId)}/memory`);
  }

  getArtifacts(conversationId: string): Observable<AgentArtifact[]> {
    return this.transport.json<AgentArtifact[]>('GET', `threads/${encodeURIComponent(conversationId)}/artifacts`);
  }

  /** Downloads one artifact version with the host's credentials. */
  downloadArtifact(conversationId: string, artifactId: string): Promise<Blob> {
    const path = `threads/${encodeURIComponent(conversationId)}/artifacts/${encodeURIComponent(artifactId)}/content`;
    return this.transport.fetch('GET', path, '*/*', undefined, new AbortController().signal).then(response => {
      if (!response.ok) {
        throw new Error(`Artifact download failed with status ${response.status}.`);
      }
      return response.blob();
    });
  }

  /** Cancels a run at its next safe point and withdraws its open proposals; needs the participant role. */
  cancelRun(runId: string): Observable<void> {
    return this.transport.json<void>('POST', `runs/${encodeURIComponent(runId)}/cancel`);
  }

  /** Resumes or restarts a failed or cancelled run; needs the approver role. */
  retryRun(runId: string, mode: AgentRunRetryMode): Observable<AgentTriggerReceipt> {
    return this.transport.json<AgentTriggerReceipt>('POST', `runs/${encodeURIComponent(runId)}/retry`, { mode });
  }

  /** Pending proposals the caller may decide, sorted by expiry. */
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
