import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  NhAssistantStore
} from '@newheap/platform-ai-chat';
import { NhAssistantAdminAgentsComponent, NhAssistantAgentsLocation } from './agents/nh-assistant-admin-agents.component';
import { NhAssistantAdminContextComponent } from './context/nh-assistant-admin-context.component';
import { NhAssistantAdminMcpServersComponent } from './mcp/nh-assistant-admin-mcp-servers.component';
import { NhAssistantAdminLocation, NhAssistantAdminTab } from './nh-assistant-admin-location';
import { startNhAssistantAdminTranslations } from './i18n/nh-assistant-admin-translations';

let nextId = 0;

/**
 * The assistant administration page with the sections Agents, Tools & connections (the MCP servers)
 * and Application context. The host routes to it and guards the route with its admin permission;
 * the component additionally shows a no-access state unless the server reports `canAdminister`.
 *
 * It opens on `initialTab`, Agents unless the host chooses otherwise. A host that keeps the section
 * and the open agent in its URL binds them to `location` and follows `locationChange`, which reports
 * every navigation of the administrator: a section, an agent's page and its tab, or the list.
 */
@Component({
  selector: 'nh-assistant-admin',
  standalone: true,
  imports: [
    TranslatePipe,
    NhAssistantIconComponent,
    NhAssistantAdminContextComponent,
    NhAssistantAdminAgentsComponent,
    NhAssistantAdminMcpServersComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin.component.html',
  styleUrl: './nh-assistant-admin.component.scss'
})
export class NhAssistantAdminComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  readonly store = inject(NhAssistantStore);

  /** Section shown first, unless `location` names one. */
  readonly initialTab = input<NhAssistantAdminTab>('agents');
  /** Where to open, for example from the host's URL; missing parts keep what is shown. */
  readonly location = input<Partial<NhAssistantAdminLocation> | null>(null);
  /** The administrator moved to another section, agent page or tab. */
  readonly locationChange = output<NhAssistantAdminLocation>();

  readonly id = `nh-assistant-admin-${nextId++}`;
  readonly tabs: readonly NhAssistantAdminTab[] = ['agents', 'mcp-servers', 'context'];
  readonly activeTab = signal<NhAssistantAdminTab | null>(null);
  readonly ready = signal(false);
  /** The agent and tab the agents section opens, as `location` names them. */
  readonly agentId = computed(() => this.location()?.agentId ?? null);
  readonly agentTab = computed(() => this.location()?.agentTab ?? null);

  constructor() {
    startNhAssistantAdminTranslations();

    afterNextRender(() => {
      void this.store.initialize().then(() => this.ready.set(true));
    });

    // A new location from the host, such as the browser's back button, wins over the section chosen here.
    effect(() => {
      this.location();
      untracked(() => this.activeTab.set(null));
    });
  }

  selected(): NhAssistantAdminTab {
    return this.activeTab() ?? this.location()?.tab ?? this.initialTab();
  }

  select(tab: NhAssistantAdminTab): void {
    if (this.selected() === tab) {
      return;
    }
    this.activeTab.set(tab);
    this.locationChange.emit({ tab, agentId: null, agentTab: null });
  }

  /** The agents section reports where the administrator went in it. */
  onAgentsNavigated(location: NhAssistantAgentsLocation): void {
    this.locationChange.emit({ tab: 'agents', ...location });
  }

  /**
   * Shows the MCP servers, for example from an agent's flow that explains why a server is not used in
   * runs without a person, and moves the focus to their tab.
   */
  showMcpServers(): void {
    this.select('mcp-servers');
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(`#${this.id}-tab-mcp-servers`)?.focus(), { injector: this.injector });
  }

  /** Arrow keys, Home and End move between tabs as the ARIA tabs pattern describes. */
  onTabKeydown(event: KeyboardEvent): void {
    const index = this.tabs.indexOf(this.selected());
    const last = this.tabs.length - 1;
    const targets: Record<string, number> = {
      ArrowRight: index === last ? 0 : index + 1,
      ArrowLeft: index === 0 ? last : index - 1,
      Home: 0,
      End: last
    };
    const next = targets[event.key];
    if (next === undefined) {
      return;
    }

    event.preventDefault();
    this.select(this.tabs[next]);
    this.host.nativeElement.querySelector<HTMLElement>(`#${this.id}-tab-${this.tabs[next]}`)?.focus();
  }
}
