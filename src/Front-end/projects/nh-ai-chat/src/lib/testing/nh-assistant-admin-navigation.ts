import { ComponentFixture } from '@angular/core/testing';

/**
 * How the specs move through the assistant administration as an administrator does: from the list
 * of agents to an agent's page and its tabs, its status and its More menu.
 */
export type AdminAgentTab = 'overview' | 'build' | 'rollout' | 'activity';

function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

/** Lets the administration render and its mock requests answer. */
export async function settleAdmin(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let round = 0; round < 6; round++) {
    fixture.detectChanges();
    await flush();
  }
  fixture.detectChanges();
}

/** The page of the agent that is open. */
export function adminAgentPage(element: HTMLElement): HTMLElement {
  const page = element.querySelector<HTMLElement>('[data-agent-page]');
  if (!page) {
    throw new Error('No agent page is open.');
  }
  return page;
}

/** Opens a tab of the agent page that is open. */
export async function openAdminTab(fixture: ComponentFixture<unknown>, element: HTMLElement, tab: AdminAgentTab): Promise<void> {
  const button = element.querySelector<HTMLButtonElement>(`[data-agent-tab="${tab}"]`);
  if (!button) {
    throw new Error(`Agent tab "${tab}" not found.`);
  }
  button.click();
  await settleAdmin(fixture);
}

/** Opens an agent's page from the list of agents, on one of its tabs. */
export async function openAdminAgent(
  fixture: ComponentFixture<unknown>,
  element: HTMLElement,
  agentId: string,
  tab: AdminAgentTab = 'overview'
): Promise<HTMLElement> {
  const row = element.querySelector<HTMLButtonElement>(`[data-agent-row="${agentId}"]`);
  if (!row) {
    throw new Error(`Agent row "${agentId}" not found.`);
  }
  row.click();
  await settleAdmin(fixture);
  if (tab !== 'overview') {
    await openAdminTab(fixture, element, tab);
  }
  return adminAgentPage(element);
}

/** Back from an agent's page to the list of agents. */
export async function backToAdminList(fixture: ComponentFixture<unknown>, element: HTMLElement): Promise<void> {
  (adminAgentPage(element).querySelector('[data-back]') as HTMLButtonElement).click();
  await settleAdmin(fixture);
}

/** Opens the status of the agent, or of one of its skills, and chooses one of its changes. */
export async function chooseAdminStatus(fixture: ComponentFixture<unknown>, scope: HTMLElement, target: string, change: string): Promise<void> {
  const toggle = scope.querySelector<HTMLButtonElement>(`[data-status-menu="${target}"]`);
  if (!toggle) {
    throw new Error(`Status of "${target}" not found.`);
  }
  toggle.click();
  await settleAdmin(fixture);
  const item = toggle.closest('.disclosure')?.querySelector<HTMLButtonElement>(change);
  if (!item) {
    throw new Error(`Status change "${change}" of "${target}" not found.`);
  }
  item.click();
  await settleAdmin(fixture);
}

/** The status a part shows in its pill: `live`, `shadow`, `paused`, `off`, `stopped` or `configured-off`. */
export function adminStatusOf(scope: HTMLElement): string | null {
  return scope.querySelector('[data-status]')?.getAttribute('data-status-value') ?? null;
}

/** Opens the More menu of the agent page and chooses one of its actions. */
export async function chooseAdminMore(fixture: ComponentFixture<unknown>, page: HTMLElement, action?: string): Promise<void> {
  (page.querySelector('[data-more]') as HTMLButtonElement).click();
  await settleAdmin(fixture);
  if (action) {
    const item = page.querySelector<HTMLButtonElement>(action);
    if (!item) {
      throw new Error(`More action "${action}" not found.`);
    }
    item.click();
    await settleAdmin(fixture);
  }
}
