import {
  ChangeDetectionStrategy,
  Component,
  ComponentRef,
  DestroyRef,
  Directive,
  EnvironmentInjector,
  Injectable,
  ViewEncapsulation,
  createComponent,
  inject
} from '@angular/core';

/**
 * The attribute that marks an element on which the theme tokens resolve (internal). The tokens
 * stylesheet selects it; `NhAssistantTokensDirective` sets it.
 */
export const NH_ASSISTANT_TOKENS_ATTRIBUTE = 'data-nh-assistant-tokens';

/**
 * The stylesheet of the theme tokens (internal). It is never placed in a template: creating it adds
 * its styles to the document through Angular's shared styles host, on the server and in the
 * browser, the way the Angular CDK loads the structural styles its components share. The styles
 * apply to the elements marked with {@link NH_ASSISTANT_TOKENS_ATTRIBUTE}.
 */
@Component({
  selector: 'nh-assistant-tokens',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  template: '',
  styleUrl: '../styles/nh-assistant-tokens.scss'
})
export class NhAssistantTokensComponent {}

/**
 * Adds the tokens stylesheet to the document once per application (internal). It stays until the
 * application is destroyed; without a marked element its rules match nothing.
 */
@Injectable({ providedIn: 'root' })
class NhAssistantTokensLoader {
  private readonly injector = inject(EnvironmentInjector);
  private stylesheet?: ComponentRef<NhAssistantTokensComponent>;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stylesheet?.destroy());
  }

  /** Adds the stylesheet unless the application has it already. */
  load(): void {
    if (this.stylesheet) {
      return;
    }

    this.stylesheet = createComponent(NhAssistantTokensComponent, { environmentInjector: this.injector });
  }
}

/**
 * Gives an element the theme tokens (internal): it marks the element, on which the tokens then
 * resolve from the host's `--nh-assistant-*` properties, and makes sure the application has the
 * tokens stylesheet. Every component of the library applies it as a host directive, so it looks
 * complete wherever it is rendered, including in a CDK overlay outside any other assistant
 * component. An element the component renders outside its own host, such as the panel's drawer in
 * the overlay container, carries the directive's attribute in the template.
 */
@Directive({
  selector: '[data-nh-assistant-tokens]',
  standalone: true,
  host: { 'data-nh-assistant-tokens': '' }
})
export class NhAssistantTokensDirective {
  constructor() {
    inject(NhAssistantTokensLoader).load();
  }
}
