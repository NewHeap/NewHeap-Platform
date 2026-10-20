import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { ConnectionRequiredPart } from '../../models/assistant-api.models';
import { nhAssistantErrorKeys, nhAssistantErrorOf, toNhAssistantError } from '../../internal/nh-assistant-errors';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NhAssistantMcpConnectService, NhAssistantMcpReturnOutcome } from '../../services/nh-assistant-mcp-connect.service';
import { NhAssistantError } from '../../services/nh-assistant.store';

let nextId = 0;

/**
 * The card of a turn that went on without an MCP server the person who asked has to connect. It
 * connects the viewer's own account, in a popup or by redirect as the host configured, and then
 * offers to ask again. After a redirect the page returns to, the card of that server shows how the
 * connection ended: connected, or the failure with its translated code.
 */
@Component({
  selector: 'nh-assistant-connection-required',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-connection-required.component.html',
  styleUrl: './nh-assistant-connection-required.component.scss',
  host: { '[attr.data-state]': 'state()' }
})
export class NhAssistantConnectionRequiredComponent {
  private readonly connections = inject(NhAssistantMcpConnectService);
  private subscription?: Subscription;

  readonly part = input.required<ConnectionRequiredPart>();
  /** Whether "Ask again" is offered once the account is connected. */
  readonly canRetry = input(true);
  /** Asks the question of the turn again after the account was connected. */
  readonly retry = output<void>();

  readonly id = `nh-assistant-connection-${nextId++}`;
  readonly state = signal<'idle' | 'connecting' | 'connected' | 'failed'>('idle');
  /** Why connecting failed, as a code and translation key. */
  readonly error = signal<NhAssistantError | null>(null);
  /** The code of `error`. */
  readonly errorCode = computed(() => this.error()?.code ?? null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  /** The connect button names connecting again when the person had connected before. */
  readonly again = computed(() => this.part().reason !== 'not-connected');

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    // The page returned from connecting this card's server in redirect mode.
    effect(() => {
      const returned = this.connections.returned();
      const serverId = this.part().serverId;
      if (returned && serverOf(returned) === serverId) {
        untracked(() => this.showReturned(returned));
      }
    });
  }

  connect(): void {
    if (this.state() === 'connecting') {
      return;
    }
    this.state.set('connecting');
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.connections.connect(this.part().serverId).subscribe({
      next: () => this.state.set('connected'),
      error: (error: unknown) => {
        this.state.set('failed');
        this.error.set(toNhAssistantError(error));
      }
    });
  }

  private showReturned(returned: NhAssistantMcpReturnOutcome): void {
    if (this.state() === 'connecting') {
      return;
    }
    if (returned.completion) {
      this.state.set('connected');
      this.error.set(null);
      return;
    }

    this.state.set('failed');
    this.error.set(nhAssistantErrorOf(returned.code));
  }
}

/** The server a returned outcome belongs to: the connected one, or the one the page left for. */
function serverOf(returned: NhAssistantMcpReturnOutcome): string | null {
  return returned.completion?.serverId ?? returned.serverId ?? null;
}
