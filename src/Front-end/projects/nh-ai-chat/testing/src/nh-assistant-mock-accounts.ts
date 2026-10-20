import { AssistantAccount } from '@newheap/platform-ai-chat';
import { NhAssistantMockAdminScenario } from './nh-assistant-mock.models';
import { NhAssistantMockResult, mockFailure as failure } from './nh-assistant-mock-admin-shared';

/**
 * The accounts of the mock administration: the scenario's `accounts`, searched by name or e-mail
 * address for technical accounts. Without accounts the search is unavailable unless the run
 * catalog says otherwise, and every account id that does not start with `unknown` is accepted.
 */
export class NhAssistantMockAccounts {
  private readonly accounts: AssistantAccount[];

  /** Whether the application searches its accounts, as the run catalog reports it. */
  readonly searchable: boolean;

  constructor(scenario: NhAssistantMockAdminScenario) {
    this.accounts = scenario.accounts ?? [];
    this.searchable = scenario.runCatalog?.accountSearch ?? this.accounts.length > 0;
  }

  /** Whether the server would refuse the account as a technical account: unknown to the application. */
  rejects(actorId: string): boolean {
    return actorId.startsWith('unknown')
      || (this.accounts.length > 0 && !this.accounts.some(account => account.actorId === actorId));
  }

  /**
   * `GET admin/accounts`: with `query` the scenario's accounts whose name or e-mail address contains
   * it, with `ids` the known accounts among them; without accounts the search is unavailable.
   */
  search(method: string, query: URLSearchParams): NhAssistantMockResult {
    if (method !== 'GET') {
      return { status: 405 };
    }
    const ids = query.get('ids');
    if (ids) {
      const wanted = new Set(ids.split(',').map(id => id.trim()).filter(id => id.length > 0));
      return { status: 200, body: this.accounts.filter(account => wanted.has(account.actorId)) };
    }
    const text = (query.get('query') ?? '').trim().toLocaleLowerCase();
    if (text.length < 2 || text.length > 100) {
      return failure(400, 'assistant-validation', { query: ['invalid'] });
    }
    if (!this.searchable) {
      return failure(404, 'assistant-account-search-unavailable');
    }
    const limit = Math.min(Math.max(Number(query.get('limit') ?? 20) || 20, 1), 50);
    return {
      status: 200,
      body: this.accounts
        .filter(account => account.displayName.toLocaleLowerCase().includes(text)
          || (account.detail ?? '').toLocaleLowerCase().includes(text))
        .slice(0, limit)
    };
  }
}
