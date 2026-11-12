import { Overlay, OverlayRef } from '@angular/cdk/overlay';
import { ComponentPortal } from '@angular/cdk/portal';
import { ComponentRef, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import * as adminApi from '../../../admin/src/public-api';
import * as agentRunsApi from '../../../agent-runs/src/public-api';
import { NhAssistantBoardHistoryComponent } from '../../../agent-runs/src/internal/nh-assistant-board-history.component';
import { NhAssistantRunNowDialogComponent } from '../../../agent-runs/src/internal/nh-assistant-run-now-dialog.component';
import { NhAssistantRunReceiptComponent } from '../../../agent-runs/src/internal/nh-assistant-run-receipt.component';
import { NhAssistantSubjectPickerComponent } from '../../../agent-runs/src/internal/nh-assistant-subject-picker.component';
import * as publicApi from '../../public-api';
import { NhAssistantElicitationCardViewComponent } from '../components/elicitation-card/nh-assistant-elicitation-card-view.component';
import { NhAssistantPlanViewComponent } from '../components/plan/nh-assistant-plan-view.component';
import { NhAssistantPresentationFieldsViewComponent } from '../components/presentation-fields/nh-assistant-presentation-fields-view.component';
import { NhAssistantToolResultCardViewComponent } from '../components/tool-result/nh-assistant-tool-result-card-view.component';
import { NhAssistantPushSwitchComponent } from '../internal/nh-assistant-push-switch.component';
import { NH_ASSISTANT_TOKENS_ATTRIBUTE, NhAssistantTokensDirective } from '../internal/nh-assistant-tokens';
import { ToolResultPresentation } from '../models/assistant-api.models';
import { provideNhAssistant } from '../provide-nh-assistant';

/**
 * The theme tokens reach the document once per application, from one stylesheet, and still resolve
 * on every component host: with the light and dark defaults, under a host's `--nh-assistant-*`
 * overrides, and in a CDK overlay outside any other assistant component.
 */
interface ComponentDefinition {
  styles: string[];
  hostDirectives: { directive: Type<unknown> }[] | null;
}

const presentation: ToolResultPresentation = {
  title: 'PRJ-1 Alpha',
  summary: null,
  fields: [],
  link: { url: '/projects/alpha', label: 'Open project' }
};

/** The light and dark defaults of the tokens the tests read, as the stylesheet declares them. */
const defaults = {
  light: { background: '#ffffff', text: '#080c1d', accent: '#022962', link: '#022962' },
  dark: { background: '#0b0e22', text: '#ffffff', accent: '#0f4087', link: '#7eb5ff' }
};

function definitionOf(component: Type<unknown>): ComponentDefinition {
  return (component as unknown as { ɵcmp: ComponentDefinition }).ɵcmp;
}

/** Every component of the three entry points that a consumer can place, plus the internal ones. */
function libraryComponents(): Type<unknown>[] {
  const exported = [publicApi, adminApi, agentRunsApi]
    .flatMap(api => Object.values(api) as unknown[])
    .filter((value): value is Type<unknown> => typeof value === 'function' && 'ɵcmp' in value);

  return [...new Set([
    ...exported,
    NhAssistantToolResultCardViewComponent,
    NhAssistantElicitationCardViewComponent,
    NhAssistantPlanViewComponent,
    NhAssistantPresentationFieldsViewComponent,
    NhAssistantPushSwitchComponent,
    NhAssistantBoardHistoryComponent,
    NhAssistantRunNowDialogComponent,
    NhAssistantRunReceiptComponent,
    NhAssistantSubjectPickerComponent
  ])];
}

/** The style elements in the document that hold the tokens. */
function tokenStyleElements(): HTMLStyleElement[] {
  return Array.from(document.head.querySelectorAll('style'))
    .filter(style => style.textContent?.includes(`[${NH_ASSISTANT_TOKENS_ATTRIBUTE}]`));
}

/** The rule of the tokens stylesheet that holds the dark defaults: its only media rule. */
function darkSchemeRule(): CSSMediaRule {
  const [style] = tokenStyleElements();
  const rules = Array.from(style.sheet?.cssRules ?? [])
    .filter((candidate): candidate is CSSMediaRule => candidate instanceof CSSMediaRule);
  expect(rules.length).withContext('the media rules of the tokens stylesheet').toBe(1);
  return rules[0];
}

/** Applies the defaults of a scheme whatever the browser prefers, through the stylesheet's own dark rule. */
function emulateScheme(scheme: 'light' | 'dark'): void {
  darkSchemeRule().media.mediaText = scheme === 'dark' ? 'all' : 'not all';
}

function token(element: Element, name: string): string {
  return getComputedStyle(element).getPropertyValue(name).trim();
}

function hex(color: string): string {
  const [red, green, blue] = (color.match(/\d+/g) ?? []).map(Number);
  return `#${[red, green, blue].map(channel => channel.toString(16).padStart(2, '0')).join('')}`;
}

describe('The theme tokens', () => {
  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideTranslateService({ fallbackLang: 'en' }),
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' })
      ]
    });
    await firstValueFrom(TestBed.inject(TranslateService).use('en'));
  });

  afterEach(() => {
    document.documentElement.style.removeProperty('--nh-assistant-accent');
    document.documentElement.style.removeProperty('--nh-assistant-link');
  });

  function renderCard(): ComponentFixture<NhAssistantToolResultCardViewComponent> {
    const fixture = TestBed.createComponent(NhAssistantToolResultCardViewComponent);
    fixture.componentRef.setInput('presentation', presentation);
    fixture.detectChanges();
    return fixture;
  }

  it('are declared once, by their own stylesheet, and by no component', () => {
    const components = libraryComponents();
    expect(components.length).toBeGreaterThan(40);

    for (const component of components) {
      const styles = definitionOf(component).styles.join('\n');
      expect(styles).withContext(component.name).not.toMatch(/--nha-background\s*:/);
      expect(styles).withContext(component.name).not.toMatch(/--nh-select-background\s*:/);
    }
  });

  it('are applied by every component whose styles use them', () => {
    // Small internal parts that only render inside another assistant component, such as the
    // message link or a document's diagram, use that component's tokens and are not listed here.
    for (const component of libraryComponents()) {
      const styles = definitionOf(component).styles.join('\n');
      // A token without a fallback needs the tokens on the host.
      if (!/var\(--nha-[a-z-]+\)/.test(styles)) {
        continue;
      }

      const directives = (definitionOf(component).hostDirectives ?? []).map(hostDirective => hostDirective.directive);
      expect(directives).withContext(component.name).toContain(NhAssistantTokensDirective);
    }
  });

  it('reach the document once, before the first change detection, however many components show', () => {
    const first = TestBed.createComponent(NhAssistantToolResultCardViewComponent);

    expect(tokenStyleElements().length).withContext('before the first change detection').toBe(1);

    first.componentRef.setInput('presentation', presentation);
    first.detectChanges();
    const second = renderCard();

    expect(tokenStyleElements().length).toBe(1);
    expect((first.nativeElement as HTMLElement).hasAttribute(NH_ASSISTANT_TOKENS_ATTRIBUTE)).toBeTrue();
    expect((second.nativeElement as HTMLElement).hasAttribute(NH_ASSISTANT_TOKENS_ATTRIBUTE)).toBeTrue();
  });

  it('resolve their light and dark defaults on a component host', () => {
    const host = renderCard().nativeElement as HTMLElement;
    expect(darkSchemeRule().conditionText).toMatch(/^\(prefers-color-scheme:\s*dark\)$/);

    for (const scheme of ['light', 'dark'] as const) {
      emulateScheme(scheme);

      for (const [name, value] of Object.entries(defaults[scheme])) {
        expect(token(host, `--nha-${name}`)).withContext(`${scheme} ${name}`).toBe(value);
      }
      expect(hex(getComputedStyle(host.querySelector('a.nh-assistant-message-link')!).color))
        .withContext(`${scheme} link`)
        .toBe(defaults[scheme].link);
    }
  });

  it('follow the browser\'s color scheme without emulation', () => {
    const host = renderCard().nativeElement as HTMLElement;
    const scheme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';

    expect(token(host, '--nha-background')).toBe(defaults[scheme].background);
  });

  it('let a host override win on :root, on an ancestor and on the component element', () => {
    const fixture = renderCard();
    const host = fixture.nativeElement as HTMLElement;
    const link = host.querySelector('a.nh-assistant-message-link') as HTMLElement;
    emulateScheme('light');

    document.documentElement.style.setProperty('--nh-assistant-accent', '#123456');
    expect(token(host, '--nha-accent')).withContext(':root').toBe('#123456');
    expect(hex(getComputedStyle(link).color)).withContext('the link follows the accent').toBe('#123456');

    host.parentElement!.style.setProperty('--nh-assistant-accent', '#654321');
    host.parentElement!.style.setProperty('--nh-assistant-link', '#0f4087');
    expect(token(host, '--nha-accent')).withContext('ancestor').toBe('#654321');
    expect(hex(getComputedStyle(link).color)).withContext('the ancestor\'s link color').toBe('#0f4087');

    host.style.setProperty('--nh-assistant-accent', '#abcdef');
    expect(token(host, '--nha-accent')).withContext('element').toBe('#abcdef');

    // The dark scheme keeps the host's link color as well.
    emulateScheme('dark');
    expect(hex(getComputedStyle(link).color)).withContext('dark').toBe('#0f4087');

    host.parentElement!.style.removeProperty('--nh-assistant-accent');
    host.parentElement!.style.removeProperty('--nh-assistant-link');
  });

  describe('in a CDK overlay', () => {
    let overlayRef: OverlayRef;
    let card: ComponentRef<NhAssistantToolResultCardViewComponent>;

    beforeEach(() => {
      overlayRef = TestBed.inject(Overlay).create();
      card = overlayRef.attach(new ComponentPortal(NhAssistantToolResultCardViewComponent));
      card.setInput('presentation', presentation);
      card.changeDetectorRef.detectChanges();
    });

    afterEach(() => overlayRef.dispose());

    it('resolve on a component outside any other assistant component', () => {
      const host = card.location.nativeElement as HTMLElement;
      expect(host.closest('.cdk-overlay-container')).not.toBeNull();
      expect(host.parentElement?.closest(`[${NH_ASSISTANT_TOKENS_ATTRIBUTE}]`)).toBeNull();
      expect(tokenStyleElements().length).toBe(1);

      for (const scheme of ['light', 'dark'] as const) {
        emulateScheme(scheme);
        expect(token(host, '--nha-background')).withContext(scheme).toBe(defaults[scheme].background);
        expect(token(host, '--nha-text')).withContext(scheme).toBe(defaults[scheme].text);
      }

      document.documentElement.style.setProperty('--nh-assistant-accent', '#123456');
      expect(token(host, '--nha-accent')).withContext('a :root override').toBe('#123456');
    });
  });
});
