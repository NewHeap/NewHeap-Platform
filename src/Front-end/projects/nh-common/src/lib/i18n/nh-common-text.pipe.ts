import {ChangeDetectorRef, OnDestroy, Pipe, PipeTransform, inject} from '@angular/core';
import {TranslateService} from '@ngx-translate/core';
import {Subscription} from 'rxjs';
import {NhCommonTextParameters, nhCommonText} from './nh-common-translations';

/**
 * Renders a library text (`nh-common.`) with `nhCommonText`: the application's translation when it
 * has one, otherwise the bundled English or Dutch text. Like ngx-translate's `translate` pipe it
 * marks its OnPush host for check when the language or the loaded translations change.
 *
 * Internal to the library components; applications use the `translate` pipe.
 */
@Pipe({
  name: 'nhCommonText',
  standalone: true,
  pure: false
})
export class NhCommonTextPipe implements PipeTransform, OnDestroy {
  private readonly translate = inject(TranslateService, {optional: true});
  private readonly changeDetector = inject(ChangeDetectorRef);
  private readonly subscription = new Subscription();
  private lastKey: string | undefined;
  private lastParameters: string | undefined;
  private lastValue = '';

  constructor() {
    if (!this.translate) {
      return;
    }

    const invalidate = () => {
      this.lastKey = undefined;
      this.changeDetector.markForCheck();
    };

    this.subscription.add(this.translate.onLangChange.subscribe(invalidate));
    this.subscription.add(this.translate.onTranslationChange.subscribe(invalidate));
    this.subscription.add(this.translate.onFallbackLangChange.subscribe(invalidate));
  }

  transform(key: string, parameters?: NhCommonTextParameters): string {
    const serializedParameters = parameters ? JSON.stringify(parameters) : undefined;
    if (key === this.lastKey && serializedParameters === this.lastParameters) {
      return this.lastValue;
    }

    this.lastKey = key;
    this.lastParameters = serializedParameters;
    this.lastValue = nhCommonText(this.translate, key, parameters);

    return this.lastValue;
  }

  ngOnDestroy(): void {
    this.subscription.unsubscribe();
  }
}
