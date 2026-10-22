import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { FileLimits, ɵNhAssistantIconComponent as NhAssistantIconComponent } from '@newheap/platform-ai-chat';
import { FileLimitField, FileLimitsDraft, RunConfigErrors, fieldErrorKey, fileLimitFields, megabytesText } from './run-config-shared';

/** How a form shows one file limit. */
interface FileLimitControl {
  field: FileLimitField;
  /** The id suffix of the control. */
  key: string;
  label: string;
  step: string;
  inputMode: 'decimal' | 'numeric';
}

const controls: Record<FileLimitField, FileLimitControl> = {
  maxFileBytes: { field: 'maxFileBytes', key: 'file-size', label: 'max-file-megabytes', step: 'any', inputMode: 'decimal' },
  maxFilePages: { field: 'maxFilePages', key: 'file-pages', label: 'max-file-pages', step: '1', inputMode: 'numeric' },
  maxFilesPerTurn: { field: 'maxFilesPerTurn', key: 'files-per-turn', label: 'max-files-per-turn', step: '1', inputMode: 'numeric' },
  maxFileTokensPerTurn: { field: 'maxFileTokensPerTurn', key: 'file-tokens', label: 'max-file-tokens-per-turn', step: '1', inputMode: 'numeric' }
};

/**
 * The lower file limits of an agent or a skill that accepts files, next to its "Accepts files"
 * switch: the largest file in megabytes, the most pages of one file, the most files of one turn and
 * the most file tokens of one turn. Each field names the application's limit, which it can only
 * lower; an empty field keeps it. Errors appear at their fields with the form's other errors.
 */
@Component({
  selector: 'nh-assistant-file-limits-fields',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-file-limits-fields.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './run-config-forms.scss']
})
export class NhAssistantFileLimitsFieldsComponent {
  /** The form's limits. */
  readonly draft = input.required<FileLimitsDraft>();
  /** The application's limits from the run catalog; `null` on older servers. */
  readonly application = input<FileLimits | null | undefined>(null);
  /** The form's field errors, by server field name. */
  readonly errors = input<RunConfigErrors>({});
  /** The prefix of the controls' ids. */
  readonly idPrefix = input.required<string>();
  /** The hint under the title: what an empty field keeps. */
  readonly hintKey = input.required<string>();
  /** A field the administrator changed, so the form clears its error. */
  readonly touched = output<FileLimitField>();

  readonly controls = fileLimitFields.map(field => controls[field]);

  idOf(control: FileLimitControl, suffix = ''): string {
    return `${this.idPrefix()}-${control.key}${suffix}`;
  }

  /** The application's limit as the field shows it: megabytes for the file size. */
  applicationLimit(control: FileLimitControl): string | null {
    const application = this.application();
    if (!application) {
      return null;
    }
    const value = application[control.field];
    return control.field === 'maxFileBytes' ? `${megabytesText(value)} MB` : String(value);
  }

  /** The upper bound a browser shows for the field. */
  max(control: FileLimitControl): number | null {
    const application = this.application();
    if (!application) {
      return null;
    }
    return control.field === 'maxFileBytes' ? application.maxFileBytes / (1024 * 1024) : application[control.field];
  }

  errorKey(control: FileLimitControl): string | null {
    return fieldErrorKey(this.errors()[control.field]);
  }

  errorParams(control: FileLimitControl): Record<string, unknown> | undefined {
    return this.errors()[control.field]?.params;
  }

  describedBy(control: FileLimitControl): string {
    const ids = [`${this.idPrefix()}-file-limits-hint`];
    if (this.application()) {
      ids.push(this.idOf(control, '-limit'));
    }
    if (this.errors()[control.field]) {
      ids.push(this.idOf(control, '-error'));
    }
    return ids.join(' ');
  }

  change(control: FileLimitControl, event: Event): void {
    this.draft().text(control.field).set((event.target as HTMLInputElement).value);
    this.touched.emit(control.field);
  }
}
