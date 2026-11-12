import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  ElicitationField,
  ElicitationPart,
  ElicitationStatus
} from '../../models/assistant-api.models';
import { NhAssistantIconComponent, NhAssistantIconName } from '../../internal/nh-assistant-icon.component';
import { formatNhAssistantCountdown } from '../approval-card/nh-assistant-approval-card.component';
import type { NhAssistantElicitationAnswer } from './nh-assistant-elicitation-card.component';
import { NhAssistantTokensDirective } from '../../internal/nh-assistant-tokens';

let nextId = 0;

/** A value the person entered in one field, before it is sent. */
type NhAssistantElicitationValue = string | number | boolean;

const statusIcons: Record<ElicitationStatus, NhAssistantIconName> = {
  pending: 'clock',
  accepted: 'check',
  declined: 'x',
  cancelled: 'stop',
  expired: 'x'
};

/**
 * The card of `nh-assistant-elicitation-card` (internal), which loads it on first use: the server's own
 * message attributed to the server, who receives the answer, the form or link, and the countdown to the
 * end of the tool call's wait. Only the person whose turn it is answers (`canAnswer`).
 */
@Component({
  selector: 'nh-assistant-elicitation-card-view',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-elicitation-card-view.component.html',
  styleUrl: './nh-assistant-elicitation-card-view.component.scss',
  host: {
    '[attr.data-status]': 'effectiveStatus()',
    '[attr.data-mode]': 'elicitation().mode'
  }
})
export class NhAssistantElicitationCardViewComponent {
  readonly elicitation = input.required<ElicitationPart>();
  /** True when the current user is the person whose turn it is and may answer. */
  readonly canAnswer = input(false);
  /** True while the host sends an answer; disables the actions. */
  readonly answering = input(false);
  /** The person's answer; emitted at most once until the host reports it is no longer answering. */
  readonly answer = output<NhAssistantElicitationAnswer>();

  readonly cardId = `nh-assistant-elicitation-${nextId++}`;
  private readonly now = signal(Date.now());
  private readonly submitted = signal(false);
  private readonly values = signal<Record<string, NhAssistantElicitationValue | null>>({});
  /** Set after a send that did not fit, so the fields show what is missing. */
  readonly showErrors = signal(false);

  readonly remainingMs = computed(() => Date.parse(this.elicitation().expiresAt) - this.now());
  /** A pending request whose wait ended reads as expired before the server says so. */
  readonly effectiveStatus = computed<ElicitationStatus>(() =>
    this.elicitation().status === 'pending' && this.remainingMs() <= 0 ? 'expired' : this.elicitation().status);
  readonly actionable = computed(() => this.effectiveStatus() === 'pending' && this.canAnswer());
  readonly statusIcon = computed(() => statusIcons[this.effectiveStatus()]);
  readonly disabled = computed(() => !this.actionable() || this.answering() || this.submitted());
  readonly countdown = computed(() => formatNhAssistantCountdown(this.remainingMs()));
  /** The fields whose value does not fit, by name. */
  readonly invalid = computed(() => {
    const values = this.values();
    return new Set(this.elicitation().fields.filter(field => !fits(field, values[field.name] ?? null)).map(field => field.name));
  });

  constructor() {
    // Tick only while the request waits.
    effect(onCleanup => {
      if (this.elicitation().status !== 'pending') {
        return;
      }

      const expiresAt = Date.parse(this.elicitation().expiresAt);
      const timer = setInterval(() => {
        this.now.set(Date.now());
        if (Date.now() >= expiresAt) {
          clearInterval(timer);
        }
      }, 1000);
      onCleanup(() => clearInterval(timer));
    });

    // Allow another attempt once the host finished and the request still waits.
    effect(() => {
      if (!this.answering() && this.elicitation().status === 'pending') {
        this.submitted.set(false);
      }
    });
  }

  fieldId(field: ElicitationField): string {
    return `${this.cardId}-${field.name}`;
  }

  valueOf(field: ElicitationField): NhAssistantElicitationValue | null {
    return this.values()[field.name] ?? null;
  }

  /** Keeps what the person entered in a field, typed as the field asks. */
  setValue(field: ElicitationField, raw: string | boolean): void {
    let value: NhAssistantElicitationValue | null;
    if (field.type === 'boolean') {
      value = raw === true;
    } else if (field.type === 'number' || field.type === 'integer') {
      const text = String(raw).trim();
      value = text === '' ? null : Number(text);
    } else {
      value = String(raw) === '' ? null : String(raw);
    }

    this.values.update(current => ({ ...current, [field.name]: value }));
  }

  /** Sends the form when every field fits; otherwise shows what does not. */
  submit(): void {
    if (this.disabled()) {
      return;
    }
    if (this.invalid().size > 0) {
      this.showErrors.set(true);
      return;
    }

    const content: Record<string, NhAssistantElicitationValue> = {};
    for (const field of this.elicitation().fields) {
      const value = this.valueOf(field) ?? (field.type === 'boolean' ? false : null);
      if (value !== null) {
        content[field.name] = value;
      }
    }

    this.emit({ action: 'accept', content });
  }

  /** The person finished the server's page. */
  done(): void {
    if (!this.disabled()) {
      this.emit({ action: 'accept' });
    }
  }

  decline(): void {
    if (!this.disabled()) {
      this.emit({ action: 'decline' });
    }
  }

  cancel(): void {
    if (!this.disabled()) {
      this.emit({ action: 'cancel' });
    }
  }

  private emit(answer: NhAssistantElicitationAnswer): void {
    this.submitted.set(true);
    this.answer.emit(answer);
  }
}

/** Whether a value fits its field: required, typed and within the field's bounds. */
function fits(field: ElicitationField, value: NhAssistantElicitationValue | null): boolean {
  if (value === null) {
    return !field.required || field.type === 'boolean';
  }

  switch (field.type) {
    case 'string':
      return typeof value === 'string'
        && (field.minLength == null || value.length >= field.minLength)
        && (field.maxLength == null || value.length <= field.maxLength);
    case 'number':
    case 'integer':
      return typeof value === 'number'
        && Number.isFinite(value)
        && (field.type !== 'integer' || Number.isInteger(value))
        && (field.minimum == null || value >= field.minimum)
        && (field.maximum == null || value <= field.maximum);
    case 'boolean':
      return typeof value === 'boolean';
    case 'enum':
      return typeof value === 'string' && (field.options ?? []).some(option => option.value === value);
    default:
      return false;
  }
}
