import {Component} from '@angular/core';
import {ComponentFixture, TestBed} from '@angular/core/testing';

import {NhModalContentDirective} from '../../directives/nh-modal.directives';
import {NhLoaderComponent} from '../nh-loader/component';
import {NhModalComponent} from './component';

@Component({
  selector: 'nh-test-tall-modal-content',
  template: '<div style="height: 3000px">A form taller than the page.</div>',
  standalone: false
})
class TallModalContentComponent {
}

/** Waits until the modal finished sliding in, which it starts 50 ms after its view. */
function slideIn(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, 80));
}

describe('NhModalComponent layout', () => {
  let scrollbars: HTMLStyleElement;
  let frame: HTMLDivElement;
  let fixture: ComponentFixture<NhModalComponent<TallModalContentComponent>> | undefined;

  beforeEach(() => {
    // Classic scrollbars, as on desktop browsers, so the holder's scrollbar takes room.
    scrollbars = document.createElement('style');
    scrollbars.textContent = '.nh-modal-holder::-webkit-scrollbar { width: 17px; }';
    document.head.appendChild(scrollbars);
    TestBed.configureTestingModule({
      declarations: [NhModalComponent, NhModalContentDirective, NhLoaderComponent, TallModalContentComponent]
    });
  });

  afterEach(() => {
    fixture?.destroy();
    fixture = undefined;
    frame?.remove();
    scrollbars.remove();
  });

  // The page itself, where the viewport's own media queries apply, and a narrow page inside it.
  for (const page of ['the page', '375px'] as const) {
    it(`keeps a large modal with tall content inside its holder next to the scrollbar on ${page}`, async () => {
      // A transformed frame is the containing block of the fixed holder: a page of this width.
      const width = page === 'the page' ? document.documentElement.clientWidth : 375;
      frame = document.createElement('div');
      frame.style.cssText = `position: relative; width: ${width}px; height: 600px; transform: translateZ(0); overflow: hidden;`;
      document.body.appendChild(frame);
      fixture = TestBed.createComponent<NhModalComponent<TallModalContentComponent>>(NhModalComponent);
      frame.appendChild(fixture.nativeElement);
      fixture.componentInstance.title = 'Edit project';
      fixture.componentInstance.modalClasses = 'large';
      fixture.detectChanges();
      fixture.componentInstance.setContentComponent(TallModalContentComponent);
      fixture.detectChanges();
      await slideIn();

      const element = fixture.nativeElement as HTMLElement;
      const holder = element.querySelector('.nh-modal-holder') as HTMLElement;
      const modal = element.querySelector('.nh-modal') as HTMLElement;
      const contentRight = holder.getBoundingClientRect().left + holder.clientWidth;

      // The content scrolls vertically, so the holder shows its scrollbar.
      expect(holder.scrollHeight).toBeGreaterThan(holder.clientHeight);
      expect(holder.offsetWidth - holder.clientWidth).toBeGreaterThan(0);
      // The modal never reaches under the scrollbar, so nothing scrolls sideways.
      expect(modal.getBoundingClientRect().right).toBeLessThanOrEqual(contentRight + 0.5);
      expect(holder.scrollWidth).toBeLessThanOrEqual(holder.clientWidth);
    });
  }
});
