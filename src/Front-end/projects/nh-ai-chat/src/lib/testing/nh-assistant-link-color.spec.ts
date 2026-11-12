import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslateService, provideTranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { NhAssistantToolResultCardViewComponent } from '../components/tool-result/nh-assistant-tool-result-card-view.component';
import { NhAssistantTokensComponent } from '../internal/nh-assistant-tokens';
import { provideNhAssistant } from '../provide-nh-assistant';

/**
 * The link color (`--nh-assistant-link`, private `--nha-link`): links and other accent-colored text
 * read on the assistant's surfaces in both schemes. The defaults are read from the compiled tokens
 * stylesheet, which every component receives, so the check follows the tokens.
 */
type Scheme = Record<string, string>;

function luminance(hex: string): number {
  const channels = [1, 3, 5]
    .map(start => parseInt(hex.slice(start, start + 2), 16) / 255)
    .map(value => value <= 0.03928 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4));
  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

/** The WCAG contrast ratio of two `#rrggbb` colors. */
function contrast(first: string, second: string): number {
  const [lighter, darker] = [luminance(first), luminance(second)].sort((left, right) => right - left);
  return (lighter + 0.05) / (darker + 0.05);
}

/** The defaults of the `--nha-*` tokens in the compiled tokens stylesheet, by scheme. */
function tokenDefaults(): { light: Scheme; dark: Scheme } {
  const css = (NhAssistantTokensComponent as unknown as { ɵcmp: { styles: string[] } }).ɵcmp.styles.join('\n');
  const darkStart = css.search(/@media\s*\(prefers-color-scheme:\s*dark\)/);
  expect(darkStart).toBeGreaterThan(0);

  const read = (block: string): Scheme => Object.fromEntries(
    [...block.matchAll(/--nha-([a-z-]+):\s*var\(--nh-assistant-[a-z-]+,\s*(.+?)\)\s*[;}]/g)]
      .map(match => [match[1], match[2].trim()]));
  return { light: read(css.slice(0, darkStart)), dark: read(css.slice(darkStart)) };
}

/** A default that names another token, such as `var(--nha-accent)`, resolved within its scheme. */
function resolve(scheme: Scheme, value: string): string {
  const reference = /^var\(--nha-([a-z-]+)\)$/.exec(value);
  return reference ? resolve(scheme, scheme[reference[1]]) : value;
}

describe('The link color', () => {
  it('defaults to the accent in the light scheme and to its own light blue in the dark scheme', () => {
    const { light, dark } = tokenDefaults();

    expect(light['link']).toBe('var(--nha-accent)');
    expect(dark['link']).toMatch(/^#[0-9a-f]{6}$/i);
    expect(dark['link']).not.toBe(dark['accent']);
  });

  it('keeps 4.5:1 on the background and surfaces of both schemes, where the dark accent does not', () => {
    const { light, dark } = tokenDefaults();
    const surfaces = ['background', 'surface', 'surface-strong'];

    expect(surfaces.map(surface => dark[surface])).toEqual(['#0b0e22', '#11152d', '#181d3b']);
    for (const surface of surfaces) {
      expect(contrast(resolve(light, light['link']), light[surface])).withContext(`light ${surface}`).toBeGreaterThanOrEqual(4.5);
      expect(contrast(dark['link'], dark[surface])).withContext(`dark ${surface}`).toBeGreaterThanOrEqual(4.5);
      // Why the link has a token of its own: the accent that fills buttons is unreadable as text.
      expect(contrast(dark['accent'], dark[surface])).withContext(`dark accent on ${surface}`).toBeLessThan(4.5);
    }
  });

  describe('of a card\'s link', () => {
    async function linkColor(properties: Record<string, string>): Promise<string> {
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          provideTranslateService({ fallbackLang: 'en' }),
          provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => 'token' })
        ]
      });
      await firstValueFrom(TestBed.inject(TranslateService).use('en'));
      const fixture = TestBed.createComponent(NhAssistantToolResultCardViewComponent);
      for (const [name, value] of Object.entries(properties)) {
        (fixture.nativeElement as HTMLElement).style.setProperty(name, value);
      }
      fixture.componentRef.setInput('presentation', {
        title: 'PRJ-1 Alpha',
        summary: null,
        fields: [],
        link: { url: '/projects/alpha', label: 'Open project' }
      });
      fixture.detectChanges();

      const link = (fixture.nativeElement as HTMLElement).querySelector('a.nh-assistant-message-link') as HTMLElement;
      return getComputedStyle(link).color;
    }

    it('follows the host\'s link color rather than its accent', async () => {
      expect(await linkColor({ '--nh-assistant-accent': '#022962', '--nh-assistant-link': '#0f4087' })).toBe('rgb(15, 64, 135)');
    });

    it('falls back to the accent in the light scheme and to the light blue in the dark scheme', async () => {
      const dark = matchMedia('(prefers-color-scheme: dark)').matches;

      expect(await linkColor({ '--nh-assistant-accent': '#022962' })).toBe(dark ? 'rgb(126, 181, 255)' : 'rgb(2, 41, 98)');
    });
  });
});
