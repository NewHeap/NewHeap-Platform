import {Component} from '@angular/core';
import {ComponentFixture, TestBed} from '@angular/core/testing';
import {provideTranslateService} from '@ngx-translate/core';
import {NhSkeletonComponent, NhSkeletonShape} from './component';

@Component({
  selector: 'nh-test-skeleton-host',
  standalone: true,
  imports: [NhSkeletonComponent],
  template: `<nh-skeleton [shape]="shape" [count]="count" [width]="width" [height]="height" [label]="label" [announce]="announce" />`
})
class SkeletonHostComponent {
  shape: NhSkeletonShape = 'line';
  count = 1;
  width: string | undefined;
  height: string | undefined;
  label: string | undefined;
  announce = true;
}

/** The style rules of the document, including those inside media rules, with their media condition. */
function styleRules(): {media: string | undefined; rule: CSSStyleRule}[] {
  const result: {media: string | undefined; rule: CSSStyleRule}[] = [];
  const visit = (rules: CSSRuleList, media: string | undefined) => {
    for (const rule of Array.from(rules)) {
      if (rule instanceof CSSStyleRule) {
        result.push({media, rule});
      } else if (rule instanceof CSSMediaRule) {
        visit(rule.cssRules, rule.conditionText);
      }
    }
  };

  for (const sheet of Array.from(document.styleSheets)) {
    try {
      visit(sheet.cssRules, undefined);
    } catch {
      // A cross-origin stylesheet cannot be read and has no skeleton rules.
    }
  }

  return result;
}

describe('NhSkeletonComponent', () => {
  let fixture: ComponentFixture<SkeletonHostComponent>;
  let host: SkeletonHostComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SkeletonHostComponent],
      providers: [provideTranslateService({fallbackLang: 'en'})]
    });

    fixture = TestBed.createComponent(SkeletonHostComponent);
    host = fixture.componentInstance;
  });

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function status(): HTMLElement | null {
    return element().querySelector('.nh-skeleton__status');
  }

  it('hides the placeholder shapes from assistive technology', () => {
    host.count = 3;
    fixture.detectChanges();

    const placeholder = element().querySelector('.nh-skeleton')!;
    expect(placeholder.getAttribute('aria-hidden')).toBe('true');
    expect(placeholder.getAttribute('data-shape')).toBe('line');
    expect(placeholder.querySelectorAll('.nh-skeleton__bone--line').length).toBe(3);
    expect(placeholder.contains(status())).toBeFalse();
  });

  it('renders list rows of an avatar with two lines', () => {
    host.shape = 'row';
    host.count = 4;
    fixture.detectChanges();

    const rows = element().querySelectorAll('.nh-skeleton__row');
    expect(rows.length).toBe(4);
    expect(rows[0].querySelectorAll('.nh-skeleton__bone--avatar').length).toBe(1);
    expect(rows[0].querySelectorAll('.nh-skeleton__bone--line').length).toBe(2);
  });

  it('sizes blocks and avatars with the width and height inputs', () => {
    host.shape = 'block';
    host.width = '12rem';
    host.height = '3rem';
    fixture.detectChanges();

    const block = element().querySelector<HTMLElement>('.nh-skeleton__bone--block')!;
    expect(block.style.width).toBe('12rem');
    expect(block.style.height).toBe('3rem');

    host.shape = 'avatar';
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();

    expect(element().querySelector('.nh-skeleton__bone--avatar')).not.toBeNull();
  });

  it('announces "Loading…" in a separate polite status after the first render', async () => {
    fixture.detectChanges();
    expect(status()?.getAttribute('role')).toBe('status');

    await fixture.whenStable();
    fixture.detectChanges();

    expect(status()?.textContent?.trim()).toBe('Loading…');
  });

  it('announces the label input instead, or nothing when announce is off', async () => {
    host.label = 'Loading projects';
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(status()?.textContent?.trim()).toBe('Loading projects');

    host.announce = false;
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
    expect(status()).toBeNull();
  });

  it('only animates the shimmer without a reduced-motion preference', () => {
    fixture.detectChanges();

    const boneRules = styleRules().filter(({rule}) => rule.selectorText.includes('nh-skeleton__bone'));
    const animated = boneRules.filter(({rule}) => rule.style.animationName && rule.style.animationName !== 'none');

    expect(animated.length).toBeGreaterThan(0);
    expect(animated.every(({media}) => media?.includes('prefers-reduced-motion: no-preference'))).toBeTrue();
  });
});
