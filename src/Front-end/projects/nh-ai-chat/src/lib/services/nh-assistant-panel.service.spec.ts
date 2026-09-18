import { OverlayContainer } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import { Component, TemplateRef, ViewContainerRef, inject, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { provideNhAssistant } from '../provide-nh-assistant';
import { NhAssistantApiService } from './nh-assistant-api.service';
import { NhAssistantPanelService } from './nh-assistant-panel.service';

@Component({
  standalone: true,
  template: `
    <button id="opener" type="button">Open</button>
    <ng-template #drawer><section class="drawer"><textarea data-nh-assistant-autofocus></textarea></section></ng-template>
  `
})
class PanelHostComponent {
  readonly drawer = viewChild.required<TemplateRef<unknown>>('drawer');
  readonly viewContainerRef = inject(ViewContainerRef);
}

describe('NhAssistantPanelService', () => {
  let service: NhAssistantPanelService;
  let overlayElement: HTMLElement;

  beforeEach(() => {
    const api = jasmine.createSpyObj<NhAssistantApiService>('NhAssistantApiService', ['status', 'getConversation']);
    api.status.and.returnValue(of({ enabled: true, agents: [], limits: { maxMessageChars: 10, maxToolCallsPerTurn: 1 } }));

    TestBed.configureTestingModule({
      imports: [PanelHostComponent],
      providers: [
        provideNhAssistant({ apiBaseUrl: '/api/assistant', getAccessToken: () => null }),
        { provide: NhAssistantApiService, useValue: api }
      ]
    });

    const fixture = TestBed.createComponent(PanelHostComponent);
    fixture.detectChanges();
    service = TestBed.inject(NhAssistantPanelService);
    service.registerPanel(new TemplatePortal(fixture.componentInstance.drawer(), fixture.componentInstance.viewContainerRef));
    overlayElement = TestBed.inject(OverlayContainer).getContainerElement();
  });

  it('opens a right-hand drawer of 420 px and closes it again', () => {
    service.open();

    const pane = overlayElement.querySelector<HTMLElement>('.nh-assistant-overlay-pane');
    expect(service.isOpen()).toBeTrue();
    expect(pane?.querySelector('.drawer')).not.toBeNull();
    expect(pane?.style.width).toBe('420px');

    service.close();

    expect(service.isOpen()).toBeFalse();
    expect(overlayElement.querySelector('.drawer')).toBeNull();
  });

  it('toggles', () => {
    service.toggle();
    expect(service.isOpen()).toBeTrue();

    service.toggle();
    expect(service.isOpen()).toBeFalse();
  });

  it('closes on Escape and returns focus to the opener', () => {
    const opener = document.getElementById('opener') as HTMLButtonElement;
    opener.focus();
    service.open();

    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(service.isOpen()).toBeFalse();
    expect(document.activeElement).toBe(opener);
  });
});
