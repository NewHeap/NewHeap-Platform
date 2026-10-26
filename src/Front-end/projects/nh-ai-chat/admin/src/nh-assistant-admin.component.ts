import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, inject, input, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  NhAssistantStore
} from '@newheap/platform-ai-chat';
import { NhAssistantAdminAgentsComponent } from './agents/nh-assistant-admin-agents.component';
import { NhAssistantAdminContextComponent } from './context/nh-assistant-admin-context.component';
import { NhAssistantAdminMcpServersComponent } from './mcp/nh-assistant-admin-mcp-servers.component';
import { startNhAssistantAdminTranslations } from './i18n/nh-assistant-admin-translations';

export type NhAssistantAdminTab = 'context' | 'agents' | 'mcp-servers';

let nextId = 0;

/**
 * The assistant administration page with the tabs Context, Agents and MCP servers. The host
 * routes to it and guards the route with its admin permission; the component additionally
 * shows a no-access state unless the server reports `canAdminister`.
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

  /** Tab shown first. */
  readonly initialTab = input<NhAssistantAdminTab>('context');

  readonly id = `nh-assistant-admin-${nextId++}`;
  readonly tabs: readonly NhAssistantAdminTab[] = ['context', 'agents', 'mcp-servers'];
  readonly activeTab = signal<NhAssistantAdminTab | null>(null);
  readonly ready = signal(false);

  constructor() {
    startNhAssistantAdminTranslations();

    afterNextRender(() => {
      void this.store.initialize().then(() => this.ready.set(true));
    });
  }

  selected(): NhAssistantAdminTab {
    return this.activeTab() ?? this.initialTab();
  }

  select(tab: NhAssistantAdminTab): void {
    this.activeTab.set(tab);
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
