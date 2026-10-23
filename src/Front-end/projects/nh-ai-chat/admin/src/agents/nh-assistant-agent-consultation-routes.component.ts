import { DatePipe } from '@angular/common';
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
  untracked,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentConsultationRouteState,
  AgentConsultationRouteSwitch,
  AgentConsultationRoutesState,
  AgentState,
  NhAssistantAdminApiService,
  NhAssistantConsultationRoute,
  NhAssistantError,
  SetAgentConsultationRoutesRequest,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAccountNames, NhAssistantAccountNamesErrorComponent } from '../nh-assistant-account-names';

let nextId = 0;

/** The longest reason the server stores with a change of the routes. */
const maxReason = 500;

/** The field of `POST admin/agents/{id}/consultation-routes` that switches each route. */
const requestFields: Readonly<Record<NhAssistantConsultationRoute, keyof Omit<SetAgentConsultationRoutesRequest, 'reason'>>> = {
  'asker-decides': 'askerDecides',
  'subject-agent-rights': 'subjectAgentRights',
  'subject-asker-rights': 'subjectAskerRights'
};

/** A change of one route whose reason is being entered. */
interface RouteChange {
  route: NhAssistantConsultationRoute;
  action: AgentConsultationRouteSwitch;
  /** Whether code allows the route, which decides what returning to the code means. */
  allowed: boolean;
}

/**
 * The routes of an agent's consultations (internal to the run configuration's Consultations section):
 * when another assistant consults the agent and the agent proposes a change, a route says who decides
 * it and whose rights run it. Shown only for an agent that can be consulted
 * (`AgentState.consultationRoutes`), through a consultation in code or one defined as data. Each route
 * says whether it is on, whether that is what the consultations allow or an administrator's switch,
 * and when no consultation allows it, which keeps it off. Administrators switch a route off for all
 * consultations of the agent at once, or on, or back to what the consultations allow, each with an
 * optional reason; a refusal, such as a route no consultation allows any more, shows with the routes.
 * `refresh` reads the routes again after the consultations changed; `changed` emits the agent's state
 * after a change.
 */
@Component({
  selector: 'nh-assistant-agent-consultation-routes',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantAccountNamesErrorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-consultation-routes.component.html',
  styleUrls: ['../nh-assistant-admin-shared.scss', './run-config/run-config-forms.scss']
})
export class NhAssistantAgentConsultationRoutesComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly reasonInput = viewChild<ElementRef<HTMLInputElement>>('reasonInput');
  private loadSubscription?: Subscription;
  private actionSubscription?: Subscription;

  readonly agentId = input.required<string>();
  /** The object types of the run catalog, to name the types whose consultation allows a route. */
  readonly subjectTypes = input<readonly { id: string; name: string; pluralName?: string | null }[]>([]);
  /** Changes when the agent's consultations changed, so the routes they allow are read again. */
  readonly refresh = input(0);
  /** The agent's state after its routes changed. */
  readonly changed = output<AgentState>();

  readonly id = `nh-assistant-consultation-routes-${nextId++}`;
  readonly maxReason = maxReason;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly busy = signal(false);
  /** The routes; `null` for an agent nobody can consult, which shows nothing. */
  readonly routes = signal<AgentConsultationRoutesState | null>(null);
  readonly changing = signal<RouteChange | null>(null);
  readonly reason = signal('');
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  /** The name of the administrator who changed the routes last, once known. */
  readonly accountNames = new NhAssistantAccountNames();

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.loadSubscription?.unsubscribe();
      this.actionSubscription?.unsubscribe();
    });
    effect(() => {
      this.agentId();
      this.refresh();
      untracked(() => this.load());
    });
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = this.admin.getAgentState(this.agentId()).subscribe({
      next: state => {
        this.show(state);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  /** The types whose consultation allows the route, by name. */
  typeNames(route: AgentConsultationRouteState): string {
    const names = new Map(this.subjectTypes().map(type => [type.id, type.pluralName ?? type.name]));
    return route.subjectTypes.map(type => names.get(type) ?? type).join(', ');
  }

  /** The switch of a route's main action: off for a route that is on, otherwise on. */
  switchOf(route: AgentConsultationRouteState): AgentConsultationRouteSwitch {
    return route.on ? 'off' : 'on';
  }

  /** The explanation of a change in its form: what dropping the switch means depends on whether a consultation allows the route. */
  explainKey(change: RouteChange): string {
    const action = change.action === 'code' ? (change.allowed ? 'code-on' : 'code-off') : change.action;
    return `nh-assistant.admin.agents.consultation-routes.explain-change.${action}`;
  }

  isChanging(route: NhAssistantConsultationRoute, action: AgentConsultationRouteSwitch): boolean {
    const change = this.changing();
    return change?.route === route && change.action === action;
  }

  /** Opens the reason form of a change of one route. A route no consultation allows cannot be switched on. */
  ask(route: AgentConsultationRouteState, action: AgentConsultationRouteSwitch): void {
    if (this.busy() || (action === 'on' && !route.allowed)) {
      return;
    }

    this.error.set(null);
    this.reason.set('');
    this.changing.set({ route: route.route, action, allowed: route.allowed });
    afterNextRender(() => this.reasonInput()?.nativeElement.focus(), { injector: this.injector });
  }

  /** Closes the reason form and returns focus to the route's action. */
  cancel(): void {
    const change = this.changing();
    this.changing.set(null);
    if (change) {
      this.focusRoute(change.route);
    }
  }

  /**
   * Saves the change of the open form; the other routes keep their switch. The form stays open while
   * the server decides, and a refusal shows with the routes.
   */
  apply(): void {
    const change = this.changing();
    if (!change || this.busy()) {
      return;
    }

    const reason = this.reason().trim();
    const request: SetAgentConsultationRoutesRequest = {
      [requestFields[change.route]]: change.action,
      reason: reason.length > 0 ? reason : null
    };
    this.busy.set(true);
    this.error.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = this.admin.setConsultationRoutes(this.agentId(), request).subscribe({
      next: updated => {
        this.busy.set(false);
        this.changing.set(null);
        this.reason.set('');
        this.show(updated);
        this.changed.emit(updated);
        this.focusRoute(change.route);
      },
      error: (failure: unknown) => {
        this.busy.set(false);
        this.error.set(toNhAssistantError(failure));
      }
    });
  }

  value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  private show(state: AgentState): void {
    this.routes.set(state.consultationRoutes ?? null);
    this.accountNames.load([state.consultationRoutes?.changedBy]);
  }

  private focusRoute(route: NhAssistantConsultationRoute): void {
    afterNextRender(() => {
      const action = this.host.nativeElement.querySelector<HTMLButtonElement>(`[data-route="${route}"] [data-route-action]:not([disabled])`);
      action?.focus();
    }, { injector: this.injector });
  }
}
