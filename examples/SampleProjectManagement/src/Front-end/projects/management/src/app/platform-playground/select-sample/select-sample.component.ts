import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { CollectionHttpRequestOptions, HttpRequestOptions, NhApiService } from '@newheap/platform-common';
import { NhComboboxComponent, NhMultiselectComponent, NhSelectOption } from '@newheap/platform-select';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Observable, delay, map, of, startWith } from 'rxjs';

/** A project of the anonymous public catalog (`GET /api/public/projects`). */
interface PublicProject {
  id: string;
  key: string;
  name: string;
}

/** The value of a project that "Create" proposes; saving the form would create it. */
const newProjectPrefix = 'new:';

/**
 * SPM-397: `nh-combobox` and `nh-multiselect` of `@newheap/platform-select` in one reactive form.
 * The project combobox searches the public project catalog on the server after two characters,
 * says why a failed search failed and searches again, and offers to create a project for a name
 * the catalog does not know. The label multiselect shows chips, removes the last one with Backspace
 * and creates labels. The priority combobox is compact, keeps `0` as a value and has an explicit
 * empty choice. Values set from code keep the form pristine; only a choice makes it dirty.
 */
@Component({
  selector: 'app-select-sample',
  standalone: true,
  imports: [ReactiveFormsModule, TranslatePipe, NhComboboxComponent, NhMultiselectComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './select-sample.component.html',
  styleUrl: './select-sample.component.scss'
})
export class SelectSampleComponent {
  private readonly api = inject(NhApiService);
  private readonly translate = inject(TranslateService);

  readonly form = new FormGroup({
    project: new FormControl<string | null>(null),
    labels: new FormControl<string[]>(['bug', 'security'], { nonNullable: true }),
    priority: new FormControl<number | null>(0)
  });

  /** Searches an endpoint that fails instead of the catalog, to show the error and its retry. */
  readonly unavailable = signal(false);
  /** Projects proposed through "Create", so their names show after the list changed. */
  readonly proposedProjects = signal<NhSelectOption<string>[]>([]);
  /** Labels created through the multiselect; they become ordinary options. */
  private readonly createdLabels = signal<NhSelectOption<string>[]>([]);

  private readonly language = toSignal(this.translate.onLangChange.pipe(map(event => event.lang)), {
    initialValue: this.translate.getCurrentLang()
  });

  readonly labelOptions = computed<NhSelectOption<string>[]>(() => {
    this.language();
    return [
      { value: 'bug', label: this.text('select-label-bug'), icon: 'ph ph-bug' },
      { value: 'feature', label: this.text('select-label-feature'), icon: 'ph ph-sparkle' },
      { value: 'docs', label: this.text('select-label-docs'), icon: 'ph ph-book-open' },
      { value: 'customer', label: this.text('select-label-customer'), icon: 'ph ph-user' },
      {
        value: 'security',
        label: this.text('select-label-security'),
        description: this.text('select-label-locked'),
        icon: 'ph ph-shield-check',
        disabled: true
      },
      ...this.createdLabels()
    ];
  });

  readonly priorityOptions = computed<NhSelectOption<number>[]>(() => {
    this.language();
    return [
      { value: 0, label: this.text('select-priority-low') },
      { value: 1, label: this.text('select-priority-normal') },
      { value: 2, label: this.text('select-priority-high') }
    ];
  });

  /** The form's value and state, refreshed on every form event for this OnPush view. */
  readonly state = toSignal(this.form.events.pipe(
    startWith(null),
    map(() => JSON.stringify({
      value: this.form.getRawValue(),
      dirty: this.form.dirty,
      touched: this.form.touched,
      disabled: this.form.disabled
    }, null, 2))
  ), { requireSync: true });

  readonly searchProjects = (query: string): Observable<NhSelectOption<string>[]> => {
    if (this.unavailable()) {
      // The sample's intentional server error, answered as a problem response.
      return this.api.get<never>('/api/library-samples/http/error', new HttpRequestOptions({ withCredentials: false }));
    }

    const options = new CollectionHttpRequestOptions({ search: query, itemsPerPage: 8, withCredentials: false });
    return this.api.getCollection<PublicProject>('/api/public/projects', options).pipe(
      map(result => result.items.map(project => ({
        value: project.id,
        label: project.name,
        description: project.key,
        icon: 'ph ph-folder'
      })))
    );
  };

  /** "Create" proposes a new project by name; the form would create it when it is saved. */
  readonly proposeProject = (name: string): Observable<NhSelectOption<string>> => of({
    value: `${newProjectPrefix}${name}`,
    label: name,
    description: this.text('select-new-project'),
    icon: 'ph ph-plus-circle'
  });

  /** Creates a label after a short round trip, as a server would. */
  readonly createLabel = (name: string): Observable<NhSelectOption<string>> => of({
    value: `label-${name.toLocaleLowerCase().replace(/[^a-z0-9]+/g, '-')}`,
    label: name,
    icon: 'ph ph-tag'
  }).pipe(delay(400));

  /** A failed search shows a localized reason, never the raw problem response. */
  readonly searchErrorText = (error: unknown): string => this.text('select-search-failed', {
    status: error instanceof HttpErrorResponse ? error.status : 0
  });

  rememberProject(option: NhSelectOption<string>): void {
    this.proposedProjects.update(projects => [...projects, option]);
  }

  rememberLabel(option: NhSelectOption<string>): void {
    this.createdLabels.update(labels => [...labels, option]);
  }

  /** Values written by the form are not reported back, so the form stays pristine. */
  setFromCode(): void {
    this.form.patchValue({ labels: ['feature', 'docs', 'security'], priority: 2 });
  }

  reset(): void {
    this.form.reset();
  }

  toggleDisabled(): void {
    if (this.form.disabled) {
      this.form.enable();
    } else {
      this.form.disable();
    }
  }

  private text(key: string, params?: Record<string, unknown>): string {
    return this.translate.instant(`project.${key}`, params) as string;
  }
}
