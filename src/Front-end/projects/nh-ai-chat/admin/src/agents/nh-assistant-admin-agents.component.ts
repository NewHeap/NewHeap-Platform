import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, catchError, forkJoin, map, of } from 'rxjs';
import {
  AdminAgent,
  McpServer,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  NhAssistantStore,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError,
  RunCatalog,
  ShadowVariantPromotion,
  ToolCatalogEntry
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames, NhAssistantAccountNamesErrorComponent } from '../nh-assistant-account-names';
import { NhAssistantAgentTab } from '../nh-assistant-admin-location';
import { NhAssistantAdminAgentEditorComponent, NhAssistantAgentEditorSection } from './nh-assistant-admin-agent-editor.component';
import { NhAssistantAgentPackageExportDialogComponent } from './agent-packages/nh-assistant-agent-package-export-dialog.component';
import { NhAssistantAgentPackageImportDialogComponent } from './agent-packages/nh-assistant-agent-package-import-dialog.component';
import { NhAssistantAgentListComponent } from './page/nh-assistant-agent-list.component';
import { NhAssistantAgentPageComponent } from './page/nh-assistant-agent-page.component';
import { startNhAssistantAdminTranslations } from '../i18n/nh-assistant-admin-translations';

let nextId = 0;

/** The run catalog as `load` reads it: the catalog, or why it could not be read. */
interface CatalogRead {
  catalog: RunCatalog | null;
  error: NhAssistantError | null;
}

/** What the agents section shows: the list, the first step of a new agent, or one agent's page. */
type AgentsView =
  | { kind: 'list' }
  | { kind: 'new' }
  | { kind: 'agent'; agentId: string; tab: NhAssistantAgentTab; setup: boolean };

/** Where the agents section is, for a host that keeps it in its URL. */
export interface NhAssistantAgentsLocation {
  /** The agent whose page is open; `null` for the list. */
  agentId: string | null;
  /** The tab of the agent's page; `null` for the list. */
  agentTab: NhAssistantAgentTab | null;
}

/**
 * Agent administration. The list (`nh-assistant-agent-list`) shows every agent once, with one
 * status, where it works, where it comes from and what needs attention, searchable and filtered,
 * with each shadow variant below its agent; a row opens the agent's page
 * (`nh-assistant-agent-page`), where everything about the agent is: its status and what an
 * administrator does with it now in the header, and the tabs Overview, Build, Rollout and Activity.
 * "New agent" asks first for what the agent is (its profile) and continues on its page, with the
 * guided setup for an agent meant to work on its own. "Import package" opens the import dialog of
 * agent packages; "Export as package" on an agent's page the export dialog with that agent chosen.
 * When the run catalog cannot be read, the list says why with its code and offers to read it again;
 * until then nothing that depends on it is offered.
 *
 * A host that keeps the open agent in its URL binds `agentId` and `agentTab` and follows
 * `navigated`, which reports every navigation of the administrator; `openMcpServers` asks the host to
 * show the MCP servers.
 */
@Component({
  selector: 'nh-assistant-admin-agents',
  standalone: true,
  imports: [
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantAdminAgentEditorComponent,
    NhAssistantAccountNamesErrorComponent,
    NhAssistantAgentPackageExportDialogComponent,
    NhAssistantAgentPackageImportDialogComponent,
    NhAssistantAgentListComponent,
    NhAssistantAgentPageComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-agents.component.html',
  styleUrls: ['../nh-assistant-admin-shared.scss', './page/agent-page.scss']
})
export class NhAssistantAdminAgentsComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly store = inject(NhAssistantStore);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private subscription?: Subscription;
  private catalogSubscription?: Subscription;

  /** The agent whose page opens, for example from the host's URL; `null` for the list. */
  readonly agentId = input<string | null>(null);
  /** The tab of the agent's page that opens. */
  readonly agentTab = input<NhAssistantAgentTab | null>(null);
  /** The administrator opened the list, an agent's page or one of its tabs. */
  readonly navigated = output<NhAssistantAgentsLocation>();
  /** An agent's flow asks to show the MCP servers, whose background use it explains. */
  readonly openMcpServers = output<void>();

  readonly id = `nh-assistant-admin-agents-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly agents = signal<AdminAgent[]>([]);
  readonly tools = signal<ToolCatalogEntry[]>([]);
  readonly servers = signal<McpServer[]>([]);
  /** The run catalog: object types by name, policies and divisions; `null` when it could not be read. */
  readonly catalog = signal<RunCatalog | null>(null);
  /** Why the run catalog could not be read; `null` once it was. */
  readonly catalogError = signal<NhAssistantError | null>(null);
  readonly catalogErrorKeys = computed(() => nhAssistantErrorKeys(this.catalogError()));
  /** The run catalog is being read again after a failure. */
  readonly catalogLoading = signal(false);
  /** Whether the application offers an account search for the technical account. */
  readonly accountSearch = signal(false);
  /**
   * The names of the technical accounts and of the people who changed an agent, once known, and why
   * they are not.
   */
  readonly accountNameLookup = new NhAssistantAccountNames();
  readonly view = signal<AgentsView>({ kind: 'list' });
  /** The agent whose page is open; `null` while the agents load or when it does not exist. */
  readonly openedAgent = computed(() => {
    const view = this.view();
    return view.kind === 'agent' ? this.agents().find(agent => agent.id === view.agentId) ?? null : null;
  });
  /** The agent whose export dialog is open, chosen in it; `null` while it is closed. */
  readonly exporting = signal<AdminAgent | null>(null);
  /** Whether the import dialog of agent packages is open. */
  readonly importing = signal(false);
  /** A new agent asks first for its profile; its tools and access follow on its page. */
  readonly createSections: NhAssistantAgentEditorSection[] = ['profile'];

  constructor() {
    startNhAssistantAdminTranslations();

    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.catalogSubscription?.unsubscribe();
    });
    this.load();

    // The host's location opens the agent it names; the administrator's own navigation is reported.
    effect(() => {
      const agentId = this.agentId();
      const tab = this.agentTab();
      untracked(() => this.follow(agentId, tab));
    });
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.catalogSubscription?.unsubscribe();
    this.catalogLoading.set(false);
    this.subscription = forkJoin([
      this.admin.getAgents(),
      this.admin.getTools(),
      this.admin.getMcpServers(),
      // The agents stay usable without the catalog; the page says why it is missing.
      this.readCatalog()
    ]).subscribe({
      next: ([agents, tools, servers, read]) => {
        this.agents.set(agents);
        this.tools.set(tools);
        this.servers.set(servers);
        this.applyCatalog(read);
        this.loading.set(false);
        this.loadAccountNames(agents);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  /** Reads the run catalog again after it could not be read. */
  retryCatalog(): void {
    this.catalogLoading.set(true);
    this.catalogSubscription?.unsubscribe();
    this.catalogSubscription = this.readCatalog().subscribe(read => {
      this.catalogLoading.set(false);
      this.applyCatalog(read);
    });
  }

  create(): void {
    this.navigate({ kind: 'new' });
    this.focus('nh-assistant-admin-agent-editor input:not([disabled])');
  }

  /** Opens an agent's page on its overview, or on another tab. */
  open(agentId: string, tab: NhAssistantAgentTab = 'overview', setup = false): void {
    this.navigate({ kind: 'agent', agentId, tab, setup });
    this.focus('[data-agent-page] h3');
  }

  /** Back to the list, at the row of the agent whose page was open. */
  backToList(): void {
    const view = this.view();
    this.navigate({ kind: 'list' });
    this.refreshAgents();
    if (view.kind === 'agent') {
      this.focus(`[data-agent-row="${view.agentId}"]`);
    } else {
      this.focus('[data-new-agent]');
    }
  }

  onTabChange(tab: NhAssistantAgentTab): void {
    const view = this.view();
    if (view.kind === 'agent' && view.tab !== tab) {
      this.navigate({ ...view, tab, setup: false });
    }
  }

  /**
   * A new agent was created: its page opens, at Build with the guided setup for an agent meant to
   * work on its own.
   */
  onCreated(agent: AdminAgent, work: boolean): void {
    this.onSaved(agent);
    this.open(agent.id, work ? 'build' : 'overview', work);
  }

  onSaved(agent: AdminAgent): void {
    this.agents.update(agents => agents.some(item => item.id === agent.id)
      ? agents.map(item => item.id === agent.id ? agent : item)
      : [...agents, agent]);
    this.loadAccountNames(this.agents());
    this.refreshChat();
  }

  onRemoved(agentId: string): void {
    this.agents.update(agents => agents.filter(item => item.id !== agentId));
    this.navigate({ kind: 'list' });
    this.focus('[data-new-agent]');
    this.refreshChat();
  }

  /** A data shadow variant continues on its own page with the guided setup: it receives events only with run settings. */
  onVariantCreated(variant: AdminAgent): void {
    this.onSaved(variant);
    this.open(variant.id, 'build', true);
  }

  /**
   * A data shadow variant was promoted onto its live agent: the agent shows its new version, and the
   * variant, which was removed, leaves the list.
   */
  onVariantPromoted(promotion: ShadowVariantPromotion): void {
    this.agents.update(agents => agents
      .filter(item => item.id !== promotion.variantId)
      .map(item => item.id === promotion.agent.id ? promotion.agent : item));
    this.refreshChat();
  }

  /** Opens the export dialog with the agent chosen. */
  exportPackage(agent: AdminAgent): void {
    this.exporting.set(agent);
  }

  /** Opens the import dialog of agent packages. */
  importPackage(): void {
    this.importing.set(true);
  }

  /**
   * Reloads the agents behind the visible view, for example their skills after the run
   * configuration changed; the view stays as it is when that fails.
   */
  refreshAgents(): void {
    this.subscription?.unsubscribe();
    this.subscription = this.admin.getAgents().subscribe({
      next: agents => {
        this.agents.set(agents);
        this.loadAccountNames(agents);
      },
      error: () => undefined
    });
    this.refreshChat();
  }

  /** Follows a location the host gives, without reporting it back. */
  private follow(agentId: string | null, tab: NhAssistantAgentTab | null): void {
    const view = this.view();
    if (agentId === null) {
      if (view.kind === 'agent') {
        this.view.set({ kind: 'list' });
      }
      return;
    }
    const wanted = tab ?? 'overview';
    if (view.kind !== 'agent' || view.agentId !== agentId || view.tab !== wanted) {
      this.view.set({ kind: 'agent', agentId, tab: wanted, setup: false });
    }
  }

  private navigate(view: AgentsView): void {
    this.view.set(view);
    this.navigated.emit(view.kind === 'agent'
      ? { agentId: view.agentId, agentTab: view.tab }
      : { agentId: null, agentTab: null });
  }

  /** Looks up the names of the technical accounts and of the people who changed an agent's state. */
  private loadAccountNames(agents: readonly AdminAgent[]): void {
    this.accountNameLookup.load(agents.flatMap(agent => [
      agent.serviceAccount?.actorId,
      agent.serviceAccount?.configuredActorId,
      agent.stateChangedBy,
      agent.shadow?.changedBy
    ]));
  }

  /** The run catalog, or why it could not be read; it never errors. */
  private readCatalog(): Observable<CatalogRead> {
    return this.admin.getRunCatalog().pipe(
      map(catalog => ({ catalog, error: null })),
      catchError((error: unknown) => of({ catalog: null, error: toNhAssistantError(error) })));
  }

  private applyCatalog(read: CatalogRead): void {
    this.catalog.set(read.catalog);
    this.catalogError.set(read.error);
    this.accountSearch.set(read.catalog?.accountSearch === true);
  }

  private focus(selector: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), { injector: this.injector });
  }

  /** Agent changes affect the agent picker of the panel. */
  private refreshChat(): void {
    void this.store.reloadStatus();
  }
}
