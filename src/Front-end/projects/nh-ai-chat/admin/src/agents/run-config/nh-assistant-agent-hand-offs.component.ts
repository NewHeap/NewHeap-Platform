import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Subscription, map, merge } from 'rxjs';
import {
  AdminAgent,
  AgentHandOffs,
  AgentSubjectType,
  DataHandOff,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { agentDisplayName } from '../agent-texts';
import { HandOffFormSaved, NhAssistantHandOffFormComponent } from './nh-assistant-hand-off-form.component';

let nextId = 0;

/** Why administrators cannot add a hand-off now. */
export type HandOffsClosed = 'application' | 'needs-settings' | 'limit' | 'no-types';

/** A hand-off as the section lists it: in code read only, or defined as data. */
interface HandOffRow {
  origin: 'code' | 'data';
  subjectType: string;
  sources: string[];
  description: string | null;
  notifySource: boolean;
  maxPerSubjectPerDay: number;
  toolId: string;
  inEffect: boolean;
  problem: string | null;
  data: DataHandOff | null;
}

/**
 * The hand-offs an agent accepts (S9): those in code read only, and those defined as data, which
 * administrators add, change and remove within the ceiling in code (`UseHandOffsAsData`), each with
 * the agents that may hand off, its daily limit, the generated tool the sources select and, when it
 * no longer fits the ceiling or the code, why it is out of effect. A hand-off needs an agent with a
 * run configuration; one out of effect can only be changed or removed.
 */
@Component({
  selector: 'nh-assistant-agent-hand-offs',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantHandOffFormComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-hand-offs.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss']
})
export class NhAssistantAgentHandOffsComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly translate = inject(TranslateService);
  /** Changes with the language and with translations that load later, so the names follow them. */
  private readonly translations = toSignal(
    merge(this.translate.onLangChange, this.translate.onTranslationChange).pipe(map((_, index) => index)),
    { initialValue: -1 });
  private loadSubscription?: Subscription;
  private actionSubscription?: Subscription;

  readonly agent = input.required<AdminAgent>();
  /** Every agent of the administration, to name the sources. */
  readonly agents = input<readonly AdminAgent[]>([]);
  /** The run catalog's kinds of object, to name them. */
  readonly subjectTypes = input<readonly AgentSubjectType[]>([]);

  readonly id = `nh-assistant-hand-offs-${nextId++}`;
  readonly loading = signal(true);
  readonly loadError = signal<NhAssistantError | null>(null);
  readonly handOffs = signal<AgentHandOffs | null>(null);
  /** The form is open: `null` adds a hand-off, a hand-off changes it. */
  readonly editing = signal<DataHandOff | null | undefined>(undefined);
  readonly confirming = signal<string | null>(null);
  readonly busy = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly failedType = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly loadErrorKeys = computed(() => nhAssistantErrorKeys(this.loadError()));

  /**
   * The agents' names as the agent list shows them, by id: an agent in code in the reader's language,
   * for the sources of every hand-off and the form's sources.
   */
  readonly agentNames = computed<ReadonlyMap<string, string>>(() => {
    this.translations();
    return new Map(this.agents().map(agent => [agent.id, agentDisplayName(this.translate, agent)]));
  });
  readonly rows = computed<HandOffRow[]>(() => {
    const handOffs = this.handOffs();
    if (!handOffs) {
      return [];
    }
    return [
      ...handOffs.codeHandOffs.map(item => ({ ...item, origin: 'code' as const, inEffect: true, problem: null, data: null })),
      ...handOffs.dataHandOffs.map(item => ({ ...item, origin: 'data' as const, data: item }))
    ];
  });
  /** The kinds of object the ceiling connects the agent about that it has no hand-off about yet. */
  readonly freeTypes = computed(() => {
    const taken = new Set(this.rows().map(row => row.subjectType));
    return (this.handOffs()?.ceiling?.subjectTypes ?? []).map(item => item.subjectType).filter(type => !taken.has(type));
  });
  readonly closed = computed<HandOffsClosed | null>(() => {
    const handOffs = this.handOffs();
    if (!handOffs?.ceiling) {
      return 'application';
    }
    if (!handOffs.configured) {
      return 'needs-settings';
    }
    if (handOffs.dataHandOffs.length >= handOffs.ceiling.maxHandOffs) {
      return 'limit';
    }
    return this.freeTypes().length === 0 ? 'no-types' : null;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.actionSubscription?.unsubscribe();
    });
    // The page shows another agent's run configuration in the same place: start over for it.
    effect(() => {
      const agentId = this.agent().id;
      untracked(() => {
        this.editing.set(undefined);
        this.confirming.set(null);
        this.message.set(null);
        this.error.set(null);
        this.load(agentId);
      });
    });
  }

  load(agentId = this.agent().id): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = this.admin.getAgentHandOffs(agentId).subscribe({
      next: handOffs => {
        this.handOffs.set(handOffs);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.loadError.set(toNhAssistantError(error));
        this.loading.set(false);
      }
    });
  }

  typeName(type: string): string {
    return this.subjectTypes().find(item => item.id === type)?.name ?? type;
  }

  nameOf(agentId: string): string {
    return this.agentNames().get(agentId) ?? agentId;
  }

  add(): void {
    this.message.set(null);
    this.confirming.set(null);
    this.editing.set(null);
  }

  edit(handOff: DataHandOff): void {
    this.message.set(null);
    this.confirming.set(null);
    this.editing.set(handOff);
  }

  cancel(): void {
    this.editing.set(undefined);
  }

  onSaved(saved: HandOffFormSaved): void {
    this.editing.set(undefined);
    this.message.set(saved.added ? 'nh-assistant.admin.hand-offs.added' : 'nh-assistant.admin.hand-offs.saved');
    this.load();
  }

  confirm(subjectType: string): void {
    this.message.set(null);
    this.confirming.set(subjectType);
  }

  remove(handOff: DataHandOff): void {
    this.busy.set(true);
    this.error.set(null);
    this.failedType.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = this.admin.removeDataHandOff(this.agent().id, handOff.subjectType, { expectedVersion: handOff.version }).subscribe({
      complete: () => {
        this.busy.set(false);
        this.confirming.set(null);
        this.message.set('nh-assistant.admin.hand-offs.removed');
        this.load();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(toNhAssistantError(error));
        this.failedType.set(handOff.subjectType);
      }
    });
  }
}
