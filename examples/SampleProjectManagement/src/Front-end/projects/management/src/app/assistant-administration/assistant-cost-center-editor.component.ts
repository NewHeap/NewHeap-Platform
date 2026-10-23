import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { NhAssistantAgentMetadataSlot } from '@newheap/platform-ai-chat/admin';
import { TranslateModule } from '@ngx-translate/core';

/** The cost center as the API's `cost-center` entry stores it (`SampleAgentCostCenter`). */
interface CostCenterValue {
  code: string;
  owner?: string | null;
}

/** The pattern of a cost center code, as the API's validator checks it. */
const costCenterCode = /^CC-[0-9]{4}$/;

/** The longest owner the API accepts. */
const maxOwnerLength = 100;

/**
 * SPM-313: the portal's own editor of an agent's cost center, which the assistant administration
 * renders in the slot of the `cost-center` entry of agent metadata. It reads the value from
 * `NhAssistantAgentMetadataSlot`, checks the code as the API does before it saves, and saves through
 * the slot, which sends it with the entry's version. The API's refusals, such as
 * `value.code: unknown-cost-center`, show at their fields. An agent in code shows its value in code
 * until an administrator overrides it; removing the override books it on the code's cost center again.
 */
@Component({
  selector: 'app-assistant-cost-center-editor',
  standalone: true,
  imports: [TranslateModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="cost-center" [attr.aria-busy]="slot.saving()">
      <label class="field">
        <span>{{ 'project.assistant-cost-center-code' | translate }}</span>
        <input
          type="text"
          data-cost-center-code
          autocomplete="off"
          maxlength="7"
          [value]="code()"
          [disabled]="slot.readOnly()"
          [attr.aria-invalid]="codeError() ? true : null"
          [attr.aria-describedby]="codeError() ? id + '-code-error' : id + '-code-hint'"
          (input)="code.set(text($event).toUpperCase())" />
        @if (codeError(); as key) {
          <small class="error" [id]="id + '-code-error'" data-cost-center-code-error>{{ key | translate }}</small>
        } @else {
          <small [id]="id + '-code-hint'">{{ 'project.assistant-cost-center-code-hint' | translate }}</small>
        }
      </label>
      <label class="field">
        <span>{{ 'project.assistant-cost-center-owner' | translate }}</span>
        <input
          type="text"
          data-cost-center-owner
          [attr.maxlength]="maxOwner"
          [value]="owner()"
          [disabled]="slot.readOnly()"
          [attr.aria-invalid]="ownerError() ? true : null"
          (input)="owner.set(text($event))" />
        @if (ownerError(); as key) {
          <small class="error" data-cost-center-owner-error>{{ key | translate }}</small>
        }
      </label>
      @if (!slot.readOnly()) {
        <div class="actions">
          <button type="button" class="primary" data-cost-center-save [disabled]="slot.saving() || !changed()" (click)="save()">
            {{ 'project.assistant-cost-center-save' | translate }}
          </button>
          @if (slot.entry().source === 'override' || slot.entry().source === 'data') {
            <button type="button" class="secondary" data-cost-center-remove [disabled]="slot.saving()" (click)="slot.remove()">
              {{ (slot.entry().source === 'override' ? 'project.assistant-cost-center-use-code' : 'project.assistant-cost-center-remove') | translate }}
            </button>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .cost-center { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 200px), 1fr)); gap: 12px; }
    .field { display: grid; gap: 6px; min-width: 0; }
    .field span { color: var(--muted); font-size: 12px; font-weight: 700; }
    .field small { color: var(--muted); font-size: 12px; }
    .field small.error { color: var(--danger); }
    input {
      box-sizing: border-box;
      width: 100%;
      min-width: 0;
      border: 1px solid var(--line);
      border-radius: var(--radius-control);
      padding: 9px 10px;
      background: var(--surface);
      color: var(--ink);
      font: inherit;
    }
    input[aria-invalid='true'] { border-color: var(--danger); }
    input:disabled { opacity: .6; }
    input:focus-visible, button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
    .actions { display: flex; flex-wrap: wrap; align-items: end; gap: 8px; }
    button {
      border: 1px solid var(--line-strong);
      border-radius: var(--radius-control);
      padding: 9px 14px;
      background: var(--surface);
      color: var(--ink);
      font: inherit;
      font-weight: 700;
      cursor: pointer;
    }
    button.primary { border-color: var(--primary); background: var(--primary); color: var(--nh-white); }
    button:disabled { opacity: .55; cursor: default; }
  `]
})
export class AssistantCostCenterEditorComponent {
  private static nextId = 0;

  readonly slot = inject(NhAssistantAgentMetadataSlot);
  readonly id = `app-cost-center-${AssistantCostCenterEditorComponent.nextId++}`;
  readonly maxOwner = maxOwnerLength;
  readonly code = signal('');
  readonly owner = signal('');
  /** Whether the code was checked here and refused, before it went to the API. */
  private readonly submitted = signal(false);

  private readonly stored = computed(() => (this.slot.entry().value as CostCenterValue | null) ?? null);
  readonly changed = computed(() =>
    this.code() !== (this.stored()?.code ?? '') || this.owner() !== (this.stored()?.owner ?? ''));
  /** The first problem of the code: the API's refusal, or the pattern checked here. */
  readonly codeError = computed(() => {
    const server = this.slot.fieldErrors('code')[0];
    if (server) {
      return `project.assistant-cost-center-error-${server}`;
    }
    return this.submitted() && !costCenterCode.test(this.code()) ? 'project.assistant-cost-center-error-unknown-cost-center' : null;
  });
  readonly ownerError = computed(() => {
    const server = this.slot.fieldErrors('owner')[0];
    return server ? `project.assistant-cost-center-error-${server}` : null;
  });

  constructor() {
    // The fields follow the value the API answered: after a save, a removal or a reload.
    effect(() => {
      const value = this.stored();
      untracked(() => {
        this.code.set(value?.code ?? '');
        this.owner.set(value?.owner ?? '');
        this.submitted.set(false);
      });
    });
  }

  text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  save(): void {
    this.submitted.set(true);
    if (!costCenterCode.test(this.code())) {
      return;
    }

    const owner = this.owner().trim();
    this.slot.save({ code: this.code(), owner: owner.length > 0 ? owner : null } satisfies CostCenterValue);
  }
}
