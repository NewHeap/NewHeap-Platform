import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { ConnectionRequiredPart } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NhAssistantMcpConnectService } from '../../services/nh-assistant-mcp-connect.service';
import { NhAssistantApiError, nhAssistantErrorMessageKey } from '../../services/nh-assistant-transport';

let nextId = 0;

/**
 * The card of a turn that went on without an MCP server the person who asked has to connect. It
 * connects the viewer's own account, in a popup or by redirect as the host configured, and then
 * offers to ask again.
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
  readonly errorCode = signal<string | null>(null);
  readonly errorKeys = computed(() => {
    const code = this.errorCode();
    return code ? [nhAssistantErrorMessageKey(code), 'nh-assistant.errors.generic'] : [];
  });
  /** The connect button names connecting again when the person had connected before. */
  readonly again = computed(() => this.part().reason !== 'not-connected');

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  connect(): void {
    if (this.state() === 'connecting') {
      return;
    }
    this.state.set('connecting');
    this.errorCode.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.connections.connect(this.part().serverId).subscribe({
      next: () => this.state.set('connected'),
      error: (error: unknown) => {
        this.state.set('failed');
        this.errorCode.set(error instanceof NhAssistantApiError ? error.code : 'assistant-server');
      }
    });
  }
}
