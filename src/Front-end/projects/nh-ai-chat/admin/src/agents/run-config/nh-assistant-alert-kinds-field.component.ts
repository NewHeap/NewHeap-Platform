import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import {
  RunAlertKind,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';
import { NhAssistantPackageLockedMarkComponent } from '../agent-packages/nh-assistant-package-locked-mark.component';
import { PackageLockView } from '../agent-packages/package-parts';

/**
 * The alert kinds the run settings of an agent created in the administration list (FB93): every kind
 * the application registers, by its translated name and urgency, to tick. A listed kind the application
 * no longer registers stays ticked with a warning until it is cleared, because its alerts are refused.
 * The application declares each kind: its texts, recipients and delivery stay as they are.
 */
@Component({
  selector: 'nh-assistant-alert-kinds-field',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantPackageLockedMarkComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset data-alert-kinds [class.group]="group()" [class.field]="!group()" [disabled]="locked()" [attr.aria-describedby]="describedBy()">
      <legend>{{ 'nh-assistant.admin.run-config.alert-kinds.title' | translate }}<nh-assistant-package-locked-mark [lock]="lock()" /></legend>
      <p class="hint" [id]="idPrefix() + '-alert-kinds-hint'">{{ 'nh-assistant.admin.run-config.alert-kinds.hint' | translate }}</p>
      <!-- In their own block: a label right under the field would take the field's label style. -->
      <div class="checks">
        @for (kind of kinds(); track kind.id) {
          <label class="check described" [attr.data-alert-kind]="kind.id">
            <input type="checkbox" [checked]="selected().includes(kind.id)" [attr.aria-invalid]="errorKey() ? true : null" (change)="toggle(kind.id, $event)" />
            <span>
              <span>{{ [kind.displayNameKey] | nhAssistantTranslate: kind.id }}</span>
              <span class="muted" data-alert-kind-urgency>{{ 'nh-assistant.alerts.urgency.' + kind.urgency | translate }} · <span class="mono">{{ kind.id }}</span></span>
              @if (kind.raisedByModel) {
                <span class="muted" data-alert-kind-model>{{ 'nh-assistant.admin.run-config.alert-kinds.model' | translate }}</span>
              }
            </span>
          </label>
        }
        @for (kindId of missing(); track kindId) {
          <!-- Listed, but the application no longer registers it: its alerts are refused until it is cleared. -->
          <label class="check described" [attr.data-missing-alert-kind]="kindId">
            <input type="checkbox" [checked]="true" aria-invalid="true" (change)="toggle(kindId, $event)" />
            <span>
              <span class="mono">{{ kindId }}</span>
              <span class="error-text">{{ 'nh-assistant.admin.run-config.alert-kinds.missing' | translate }}</span>
            </span>
          </label>
        }
      </div>
      @if (kinds().length === 0 && missing().length === 0) {
        <p class="muted" data-alert-kinds-empty>{{ 'nh-assistant.admin.run-config.alert-kinds.empty' | translate }}</p>
      }
      @if (errorKey(); as key) {
        <p class="field-error" [id]="idPrefix() + '-alert-kinds-error'"><nh-assistant-icon name="warning" />{{ key | translate: errorParams() }}</p>
      }
    </fieldset>
  `,
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss']
})
export class NhAssistantAlertKindsFieldComponent {
  /** Every kind the application registers, as the run catalog lists them. */
  readonly kinds = input<readonly RunAlertKind[]>([]);
  /** The kinds the settings list, by id. */
  readonly selected = input<readonly string[]>([]);
  readonly idPrefix = input.required<string>();
  /** A group of its own in a form, instead of a field in a step's group. */
  readonly group = input(false);
  /** The agent package locks the list, which then shows read-only. */
  readonly locked = input(false);
  /** The lock the mark next to the legend shows; `null` when the list changes here. */
  readonly lock = input<PackageLockView | null>(null);
  /** The translation key of the field's error, or `null`. */
  readonly errorKey = input<string | null>(null);
  readonly errorParams = input<Record<string, unknown> | undefined>(undefined);
  /** The kinds the settings list after a tick, in the run catalog's order. */
  readonly selectedChange = output<string[]>();

  /** Listed kinds the application no longer registers. */
  readonly missing = computed(() => this.selected().filter(kindId => !this.kinds().some(kind => kind.id === kindId)));

  readonly describedBy = computed(() => [
    `${this.idPrefix()}-alert-kinds-hint`,
    this.errorKey() ? `${this.idPrefix()}-alert-kinds-error` : null
  ].filter(id => id !== null).join(' '));

  toggle(kindId: string, event: Event): void {
    const ticked = (event.target as HTMLInputElement).checked;
    const next = new Set(this.selected());
    if (ticked) {
      next.add(kindId);
    } else {
      next.delete(kindId);
    }
    // The run catalog's order, then the kinds it no longer lists.
    this.selectedChange.emit([
      ...this.kinds().map(kind => kind.id).filter(id => next.has(id)),
      ...[...next].filter(id => !this.kinds().some(kind => kind.id === id))
    ]);
  }
}

/**
 * The kinds to send with a save of run settings: `undefined` while the list is unchanged, so the server
 * keeps it, also a kind the application no longer registers; otherwise the ticked kinds, each once.
 */
export function alertKindsToSave(selected: readonly string[], stored: readonly string[] | undefined): string[] | undefined {
  const before = [...new Set(stored ?? [])].sort();
  const after = [...new Set(selected)].sort();
  return before.length === after.length && before.every((kindId, index) => kindId === after[index]) ? undefined : after;
}
