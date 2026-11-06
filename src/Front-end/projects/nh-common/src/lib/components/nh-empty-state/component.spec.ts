import {Component} from '@angular/core';
import {ComponentFixture, TestBed} from '@angular/core/testing';
import {TranslateService, provideTranslateService} from '@ngx-translate/core';
import {NH_COMMON_ICONS} from '../nh-common-icon/component';
import {NhEmptyStateComponent, NhEmptyStateHeadingLevel, NhEmptyStateVariant} from './component';

@Component({
  selector: 'nh-test-empty-state-host',
  standalone: true,
  imports: [NhEmptyStateComponent],
  template: `<nh-empty-state [variant]="variant" [heading]="heading" [description]="description" [headingLevel]="headingLevel" />`
})
class EmptyStateHostComponent {
  variant: NhEmptyStateVariant = 'no-data';
  heading: string | undefined;
  description: string | undefined;
  headingLevel: NhEmptyStateHeadingLevel | undefined;
}

@Component({
  selector: 'nh-test-empty-state-content-host',
  standalone: true,
  imports: [NhEmptyStateComponent],
  template: `
    <nh-empty-state variant="no-results">
      <i nhEmptyStateIcon class="custom-icon"></i>
      <button type="button" class="clear-filters">Clear filters</button>
    </nh-empty-state>`
})
class EmptyStateContentHostComponent {
}

describe('NhEmptyStateComponent', () => {
  let fixture: ComponentFixture<EmptyStateHostComponent>;
  let host: EmptyStateHostComponent;

  function configure(providers: unknown[] = []): void {
    TestBed.configureTestingModule({
      imports: [EmptyStateHostComponent],
      providers: [provideTranslateService({fallbackLang: 'en'}), ...(providers as never[])]
    });

    fixture = TestBed.createComponent(EmptyStateHostComponent);
    host = fixture.componentInstance;
  }

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(selector: string): string | undefined {
    return element().querySelector(selector)?.textContent?.trim();
  }

  it('shows the bundled "no data" texts in a polite status with a decorative default icon', () => {
    configure();
    fixture.detectChanges();

    expect(text('.nh-empty-state__heading')).toBe('Nothing here yet');
    expect(text('.nh-empty-state__description')).toBe('Items appear here as soon as they are added.');
    expect(element().querySelector('.nh-empty-state__text')?.getAttribute('role')).toBe('status');
    expect(element().querySelector('.nh-empty-state__icon')?.getAttribute('aria-hidden')).toBe('true');
    expect(element().querySelector('.nh-empty-state__icon svg')).not.toBeNull();
    expect(element().querySelector('.nh-empty-state__actions')?.children.length).toBe(0);
  });

  it('explains a filter without results', () => {
    configure();
    host.variant = 'no-results';
    fixture.detectChanges();

    expect(text('.nh-empty-state__heading')).toBe('No matching results');
    expect(text('.nh-empty-state__description')).toBe('Try another search term or clear the filters.');
    expect(element().querySelector('.nh-empty-state')?.getAttribute('data-variant')).toBe('no-results');
  });

  it('announces an error assertively', () => {
    configure();
    host.variant = 'error';
    fixture.detectChanges();

    expect(text('.nh-empty-state__heading')).toBe('This list could not be loaded');
    expect(element().querySelector('.nh-empty-state__text')?.getAttribute('role')).toBe('alert');
  });

  it('uses the heading and description inputs, hides an empty description and exposes a heading level', () => {
    configure();
    host.heading = 'No projects yet';
    host.description = '';
    host.headingLevel = 3;
    fixture.detectChanges();

    const heading = element().querySelector('.nh-empty-state__heading')!;
    expect(heading.textContent?.trim()).toBe('No projects yet');
    expect(heading.getAttribute('role')).toBe('heading');
    expect(heading.getAttribute('aria-level')).toBe('3');
    expect(element().querySelector('.nh-empty-state__description')).toBeNull();
  });

  it('renders the heading as a paragraph without a heading level', () => {
    configure();
    fixture.detectChanges();

    const heading = element().querySelector('.nh-empty-state__heading')!;
    expect(heading.hasAttribute('role')).toBeFalse();
    expect(heading.hasAttribute('aria-level')).toBeFalse();
  });

  it('replaces the default icon with a projected one and projects actions', () => {
    configure();
    const contentFixture = TestBed.createComponent(EmptyStateContentHostComponent);
    contentFixture.detectChanges();
    const content = contentFixture.nativeElement as HTMLElement;

    expect(content.querySelector('.nh-empty-state__icon .custom-icon')).not.toBeNull();
    expect(content.querySelector('.nh-empty-state__icon svg')).toBeNull();
    expect(content.querySelector('.nh-empty-state__actions .clear-filters')?.textContent?.trim()).toBe('Clear filters');
    expect(content.querySelector('.nh-empty-state__text .clear-filters')).toBeNull();
  });

  it('uses the icon class of the application icon library when one is configured', () => {
    configure([{provide: NH_COMMON_ICONS, useValue: {empty: 'ph ph-folder-dashed'}}]);
    fixture.detectChanges();

    const icon = element().querySelector('.nh-empty-state__icon i')!;
    expect(icon.className).toBe('ph ph-folder-dashed');
    expect(icon.getAttribute('aria-hidden')).toBe('true');
  });

  it('follows the application translation and a language change', () => {
    configure();
    const translate = TestBed.inject(TranslateService);
    translate.setTranslation('nl', {'nh-common': {'empty-state': {'no-data': {title: 'Nog geen projecten'}}}});
    fixture.detectChanges();
    expect(text('.nh-empty-state__heading')).toBe('Nothing here yet');

    translate.use('nl');
    fixture.detectChanges();

    expect(text('.nh-empty-state__heading')).toBe('Nog geen projecten');
    expect(text('.nh-empty-state__description')).toBe('Items verschijnen hier zodra ze zijn toegevoegd.');
  });
});
