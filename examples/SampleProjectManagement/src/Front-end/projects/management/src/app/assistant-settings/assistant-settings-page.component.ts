import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { Subscription, forkJoin, of } from 'rxjs';
import { catchError, finalize, map } from 'rxjs/operators';
import {
  AdminAgent,
  AgentCompositionFeature,
  AgentCompositionFeatureStatus,
  AgentCompositionStatus,
  NhAssistantAdminApiService
} from '@newheap/platform-ai-chat';
import {
  ASSISTANT_COMPOSITION_MCP_AGENT_ID,
  AssistantCompositionSettingApiService,
  AssistantCompositionSettingViewModel
} from 'sample-project-management-common';

/** One row of the page: an agent of the assistant, or the MCP exposure. */
interface SettingRow {
  agentId: string;
  name: string;
  /** An agent in code names itself with a translation key; other agents show the name as typed. */
  nameIsKey: boolean;
  /** Created in the administration: only the features the application's ceiling allows such agents. */
  createdHere: boolean;
  setting: AssistantCompositionSettingViewModel;
  /** What the assistant actually does now; `null` for the MCP exposure, which has no agent. */
  status: AgentCompositionStatus | null;
}

/**
 * SPM-371: the portal's own setting whether the assistant's agents use compositions and the script tool
 * in this environment, with what the assistant actually does next to each checkbox. The assistant asks
 * the setting at the start of every turn, so a change applies from the next one without a restart. Code
 * stays the ceiling, and the effective state says why a feature is still off.
 */
@Component({
  selector: 'app-assistant-settings-page',
  standalone: true,
  imports: [TranslateModule],
  templateUrl: './assistant-settings-page.component.html',
  styleUrl: './assistant-settings-page.component.scss'
})
export class AssistantSettingsPageComponent implements OnInit {
  private readonly settingsApi = inject(AssistantCompositionSettingApiService);
  private readonly assistantAdmin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;

  readonly features: readonly AgentCompositionFeature[] = ['compositions', 'scripts'];
  readonly rows = signal<SettingRow[]>([]);
  readonly loading = signal(true);
  readonly saving = signal<string | null>(null);
  readonly errorKey = signal<string | null>(null);
  readonly empty = computed(() => !this.loading() && this.rows().length === 0);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.errorKey.set(null);
    this.subscription?.unsubscribe();
    this.subscription = forkJoin({
      agents: this.assistantAdmin.getAgents(),
      settings: this.settingsApi.list()
    }).pipe(finalize(() => this.loading.set(false))).subscribe({
      next: ({ agents, settings }) => {
        const rows = agents
          .filter(agent => !agent.shadowOf)
          .map(agent => this.rowOf(agent, settings));
        rows.push({
          agentId: ASSISTANT_COMPOSITION_MCP_AGENT_ID,
          name: '',
          nameIsKey: false,
          createdHere: false,
          setting: settingOf(ASSISTANT_COMPOSITION_MCP_AGENT_ID, settings),
          status: null
        });
        this.rows.set(rows);
        this.loadStatuses();
      },
      error: () => this.errorKey.set('assistant-settings.load-error')
    });
  }

  /** Whether a row is the MCP exposure. */
  isMcp(row: SettingRow): boolean {
    return row.agentId === ASSISTANT_COMPOSITION_MCP_AGENT_ID;
  }

  uses(row: SettingRow, feature: AgentCompositionFeature): boolean {
    return feature === 'compositions' ? row.setting.compositions : row.setting.scripts;
  }

  statusOf(row: SettingRow, feature: AgentCompositionFeature): AgentCompositionFeatureStatus | null {
    return row.status?.features.find(item => item.feature === feature) ?? null;
  }

  /** The text of why a feature is on or off; not installed names the feature. */
  reasonKey(status: AgentCompositionFeatureStatus): string {
    const reason = status.reason === 'not-installed' ? `not-installed-${status.feature}` : status.reason;
    return `assistant-settings.reasons.${reason}`;
  }

  toggle(row: SettingRow, feature: AgentCompositionFeature, event: Event): void {
    const on = (event.target as HTMLInputElement).checked;
    const next = { compositions: row.setting.compositions, scripts: row.setting.scripts, [feature]: on };
    this.saving.set(row.agentId);
    this.errorKey.set(null);
    this.settingsApi.save(row.agentId, next)
      .pipe(finalize(() => this.saving.set(null)))
      .subscribe({
        next: saved => {
          this.replace(row.agentId, current => ({ ...current, setting: saved }));
          this.loadStatus(row.agentId);
        },
        error: () => {
          // The checkbox shows the stored setting again.
          this.rows.update(rows => [...rows]);
          this.errorKey.set('assistant-settings.save-error');
        }
      });
  }

  private rowOf(agent: AdminAgent, settings: AssistantCompositionSettingViewModel[]): SettingRow {
    return {
      agentId: agent.id,
      name: agent.displayName,
      nameIsKey: agent.source === 'code',
      createdHere: agent.source === 'admin',
      setting: settingOf(agent.id, settings),
      status: null
    };
  }

  private loadStatuses(): void {
    for (const row of this.rows()) {
      if (!this.isMcp(row)) {
        this.loadStatus(row.agentId);
      }
    }
  }

  /** The effective state of one agent; a failure leaves the state out for that agent. */
  private loadStatus(agentId: string): void {
    if (agentId === ASSISTANT_COMPOSITION_MCP_AGENT_ID) {
      return;
    }
    this.assistantAdmin.getAgentCompositionStatus(agentId)
      .pipe(map(status => status as AgentCompositionStatus | null), catchError(() => of(null)))
      .subscribe(status => this.replace(agentId, row => ({ ...row, status })));
  }

  private replace(agentId: string, change: (row: SettingRow) => SettingRow): void {
    this.rows.update(rows => rows.map(row => row.agentId === agentId ? change(row) : row));
  }
}

function settingOf(agentId: string, settings: AssistantCompositionSettingViewModel[]): AssistantCompositionSettingViewModel {
  return settings.find(setting => setting.agentId === agentId) ?? { agentId, compositions: false, scripts: false };
}
