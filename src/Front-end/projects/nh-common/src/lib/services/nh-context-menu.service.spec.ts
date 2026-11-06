import {ErrorHandler} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {provideRouter} from '@angular/router';

import {NhContextMenu, NhContextMenuItem, NhContextMenuService} from './nh-context-menu.service';

/** The space the service keeps between the menu and the edges of the viewport. */
const margin = 8;

function menu(): HTMLElement {
  return document.querySelector<HTMLElement>('.nh-context-menu')!;
}

function isOpen(): boolean {
  return document.querySelector('.nh-context-menu') !== null;
}

function menuItems(): HTMLElement[] {
  return Array.from(document.querySelectorAll<HTMLElement>('.nh-context-menu [role^="menuitem"]'));
}

/** Dispatches a keydown on the focused element, as a key press does. */
function press(key: string): KeyboardEvent {
  const event = new KeyboardEvent('keydown', {key, bubbles: true, cancelable: true});
  (document.activeElement ?? document.body).dispatchEvent(event);
  return event;
}

function pointerDown(target: Element): void {
  target.dispatchEvent(new PointerEvent('pointerdown', {bubbles: true, cancelable: true}));
}

/** Resolves after the promise callbacks queued so far, such as those of a settled action, have run. */
function settle(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve));
}

function viewport(): {width: number; height: number} {
  return {width: document.documentElement.clientWidth, height: document.documentElement.clientHeight};
}

/** Scrolls the document and resolves once the browser has reported the scroll. */
function scrollDocumentTo(top: number): Promise<void> {
  if (Math.round(window.scrollY) === top) {
    return Promise.resolve();
  }

  return new Promise(resolve => {
    window.addEventListener('scroll', () => resolve(), {once: true});
    window.scrollTo(0, top);
  });
}

describe('NhContextMenuService', () => {
  let service: NhContextMenuService;
  let errorHandler: jasmine.SpyObj<ErrorHandler>;
  let invoker: HTMLButtonElement;

  beforeEach(() => {
    errorHandler = jasmine.createSpyObj<ErrorHandler>('ErrorHandler', ['handleError']);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), {provide: ErrorHandler, useValue: errorHandler}]
    });
    service = TestBed.inject(NhContextMenuService);

    invoker = document.createElement('button');
    invoker.type = 'button';
    invoker.textContent = 'Project actions';
    document.body.appendChild(invoker);
    invoker.focus();
  });

  afterEach(() => {
    service.close();
    invoker.remove();
  });

  function open(items: NhContextMenuItem[], x = 20, y = 20): void {
    service.open(NhContextMenu.fromEvent({clientX: x, clientY: y}).withItems(items));
  }

  describe('rendering', () => {
    it('renders a title as text, so markup in user data never becomes an element', () => {
      const title = 'Project <img class="nh-injected" src="data:,">';
      open([new NhContextMenuItem({title})]);

      const item = menuItems()[0];
      expect(item.querySelector('img')).toBeNull();
      expect(document.querySelector('img.nh-injected')).toBeNull();
      expect(item.querySelector('.nh-context-menu-item-title')!.textContent).toBe(title);
    });

    it('renders iconClass as a hidden icon before the title', () => {
      open([new NhContextMenuItem({title: 'Open project', iconClass: 'ph ph-folder-open'})]);

      const icon = menuItems()[0].firstElementChild as HTMLElement;
      expect(icon.tagName).toBe('I');
      expect(icon.classList).toContain('ph');
      expect(icon.classList).toContain('ph-folder-open');
      expect(icon.getAttribute('aria-hidden')).toBe('true');
      expect(icon.nextElementSibling!.textContent).toBe('Open project');
    });

    it('exposes menu, menuitem, separator, checkbox and disabled semantics', () => {
      open([
        new NhContextMenuItem({title: 'Open'}),
        new NhContextMenuItem({type: 'divider'}),
        new NhContextMenuItem({title: 'Pinned', checked: true}),
        new NhContextMenuItem({title: 'Archived', checked: false}),
        new NhContextMenuItem({title: 'Delete', disabled: true})
      ]);

      const rendered = Array.from(menu().children) as HTMLElement[];
      expect(menu().tagName).toBe('UL');
      expect(menu().getAttribute('role')).toBe('menu');
      expect(rendered.map(element => element.getAttribute('role')))
        .toEqual(['menuitem', 'separator', 'menuitemcheckbox', 'menuitemcheckbox', 'menuitem']);
      expect(rendered[1].classList).toContain('divider');
      expect(rendered[1].hasAttribute('tabindex')).toBeFalse();
      expect(rendered[2].getAttribute('aria-checked')).toBe('true');
      expect(rendered[3].getAttribute('aria-checked')).toBe('false');
      expect(rendered[0].hasAttribute('aria-checked')).toBeFalse();
      expect(rendered[4].getAttribute('aria-disabled')).toBe('true');
    });

    it('applies the configured z-index', () => {
      service.config.zIndex = 42;
      open([new NhContextMenuItem({title: 'Open'})]);

      expect(menu().style.zIndex).toBe('42');
    });
  });

  describe('keyboard', () => {
    it('focuses the first item on open and moves with the arrow keys, Home and End, past separators', () => {
      open([
        new NhContextMenuItem({title: 'Open'}),
        new NhContextMenuItem({type: 'divider'}),
        new NhContextMenuItem({title: 'Rename'}),
        new NhContextMenuItem({title: 'Delete', disabled: true})
      ]);
      const [first, second, third] = menuItems();

      expect(document.activeElement).toBe(first);
      expect(first.tabIndex).toBe(0);
      expect(second.tabIndex).toBe(-1);

      press('ArrowDown');
      expect(document.activeElement).toBe(second);
      expect(first.tabIndex).toBe(-1);
      expect(second.tabIndex).toBe(0);

      press('ArrowDown');
      expect(document.activeElement).withContext('a disabled item stays reachable').toBe(third);

      press('ArrowDown');
      expect(document.activeElement).withContext('ArrowDown wraps to the first item').toBe(first);

      press('ArrowUp');
      expect(document.activeElement).withContext('ArrowUp wraps to the last item').toBe(third);

      press('Home');
      expect(document.activeElement).toBe(first);

      const end = press('End');
      expect(document.activeElement).toBe(third);
      expect(end.defaultPrevented).withContext('navigation keys do not scroll the page').toBeTrue();
    });

    it('activates the focused item with Enter, then closes and returns focus', async () => {
      const onClick = jasmine.createSpy('onClick').and.resolveTo();
      open([new NhContextMenuItem({title: 'Open'}), new NhContextMenuItem({title: 'Rename', onClick})]);

      press('ArrowDown');
      press('Enter');
      await settle();

      expect(onClick).toHaveBeenCalledTimes(1);
      expect(onClick.calls.mostRecent().args[0]).toEqual(jasmine.any(KeyboardEvent));
      expect(isOpen()).toBeFalse();
      expect(document.activeElement).toBe(invoker);
    });

    it('activates the focused item with Space', async () => {
      const onClick = jasmine.createSpy('onClick').and.resolveTo();
      open([new NhContextMenuItem({title: 'Open', onClick})]);

      const space = press(' ');
      await settle();

      expect(space.defaultPrevented).toBeTrue();
      expect(onClick).toHaveBeenCalledTimes(1);
      expect(isOpen()).toBeFalse();
    });

    it('closes with Escape before a modal below sees it and returns focus', () => {
      open([new NhContextMenuItem({title: 'Open'})]);

      const escape = press('Escape');

      expect(isOpen()).toBeFalse();
      expect(escape.defaultPrevented).withContext('NhModalComponent ignores a handled Escape').toBeTrue();
      expect(document.activeElement).toBe(invoker);
    });

    it('closes with Escape while focus is outside the menu', () => {
      open([new NhContextMenuItem({title: 'Open'})]);
      invoker.focus();

      press('Escape');

      expect(isOpen()).toBeFalse();
    });

    it('closes with Tab and returns focus to the element that opened it', () => {
      open([new NhContextMenuItem({title: 'Open'}), new NhContextMenuItem({title: 'Rename'})]);

      press('Tab');

      expect(isOpen()).toBeFalse();
      expect(document.activeElement).toBe(invoker);
    });

    it('does not activate a disabled item and stays open', async () => {
      const onClick = jasmine.createSpy('onClick').and.resolveTo();
      open([new NhContextMenuItem({title: 'Delete', disabled: true, onClick})]);

      press('Enter');
      menuItems()[0].click();
      await settle();

      expect(onClick).not.toHaveBeenCalled();
      expect(isOpen()).toBeTrue();
    });
  });

  describe('actions', () => {
    it('closes when the action rejects and reports the error to the ErrorHandler', async () => {
      const failure = new Error('Copy failed');
      open([new NhContextMenuItem({title: 'Copy', onClick: () => Promise.reject(failure)})]);

      menuItems()[0].click();
      await settle();

      expect(isOpen()).toBeFalse();
      expect(errorHandler.handleError).toHaveBeenCalledOnceWith(failure);
      expect(document.activeElement).toBe(invoker);
    });

    it('closes when the action throws synchronously and reports the error', async () => {
      const failure = new Error('Not a promise');
      const throwing = (() => {
        throw failure;
      }) as unknown as NhContextMenuItem['onClick'];
      open([new NhContextMenuItem({title: 'Copy', onClick: throwing})]);

      menuItems()[0].click();
      await settle();

      expect(isOpen()).toBeFalse();
      expect(errorHandler.handleError).toHaveBeenCalledOnceWith(failure);
    });

    it('stays open while the action runs and runs it once when activated twice', async () => {
      let finish!: () => void;
      const onClick = jasmine.createSpy('onClick').and.callFake(() => new Promise<void>(resolve => finish = resolve));
      open([new NhContextMenuItem({title: 'Save', onClick})]);

      menuItems()[0].click();
      press('Enter');
      await settle();

      expect(onClick).toHaveBeenCalledTimes(1);
      expect(isOpen()).toBeTrue();

      finish();
      await settle();

      expect(isOpen()).toBeFalse();
      expect(errorHandler.handleError).not.toHaveBeenCalled();
    });

    it('keeps a menu open that the action opened', async () => {
      open([new NhContextMenuItem({
        title: 'More',
        onClick: async () => open([new NhContextMenuItem({title: 'Second level as a new menu'})])
      })]);

      menuItems()[0].click();
      await settle();

      expect(isOpen()).toBeTrue();
      expect(menuItems()[0].textContent).toBe('Second level as a new menu');
    });
  });

  describe('dismissal', () => {
    it('is not closed by the click that opened it, nor by a pointer down inside it', () => {
      invoker.addEventListener('click', () => service.open(NhContextMenu.fromElement(invoker).withItems([
        new NhContextMenuItem({title: 'Open'})
      ])));

      pointerDown(invoker);
      invoker.click();
      expect(isOpen()).toBeTrue();

      pointerDown(menuItems()[0]);
      expect(isOpen()).toBeTrue();
    });

    it('closes on a pointer down outside without moving focus back', () => {
      const outside = document.createElement('input');
      document.body.appendChild(outside);
      open([new NhContextMenuItem({title: 'Open'})]);

      try {
        pointerDown(outside);

        expect(isOpen()).toBeFalse();
        expect(document.activeElement).not.toBe(invoker);
      } finally {
        outside.remove();
      }
    });

    it('closes when an inner container scrolls, but not when the menu itself scrolls', () => {
      const container = document.createElement('div');
      document.body.appendChild(container);
      open([new NhContextMenuItem({title: 'Open'})]);

      try {
        menu().dispatchEvent(new Event('scroll'));
        expect(isOpen()).toBeTrue();

        // Scroll events do not bubble; only a capture-phase listener sees this one.
        container.dispatchEvent(new Event('scroll'));
        expect(isOpen()).toBeFalse();
      } finally {
        container.remove();
      }
    });

    it('closes when the window resizes', () => {
      open([new NhContextMenuItem({title: 'Open'})]);

      window.dispatchEvent(new Event('resize'));

      expect(isOpen()).toBeFalse();
    });
  });

  describe('placement', () => {
    it('stays at the pointer while the document is scrolled', async () => {
      const spacer = document.createElement('div');
      spacer.style.height = '4000px';
      document.body.appendChild(spacer);

      try {
        await scrollDocumentTo(400);
        expect(window.scrollY).toBeGreaterThan(0);

        open([new NhContextMenuItem({title: 'Open'})], 20, 30);
        const rect = menu().getBoundingClientRect();

        expect(menu().style.position).toBe('fixed');
        expect(rect.left).toBeCloseTo(20, 0);
        expect(rect.top).toBeCloseTo(30, 0);
      } finally {
        service.close();
        await scrollDocumentTo(0);
        spacer.remove();
      }
    });

    it('flips left and up when it would overflow the bottom-right corner', () => {
      const {width, height} = viewport();
      const x = width - 20;
      const y = height - 20;
      open([new NhContextMenuItem({title: 'Open'}), new NhContextMenuItem({title: 'Rename'})], x, y);

      const rect = menu().getBoundingClientRect();
      expect(rect.right).toBeCloseTo(x, 0);
      expect(rect.bottom).toBeCloseTo(y, 0);
    });

    it('stays inside the viewport for a position far outside it', () => {
      const {width, height} = viewport();
      open([new NhContextMenuItem({title: 'Far away'})], 5000, 5000);

      const rect = menu().getBoundingClientRect();
      expect(rect.left).toBeGreaterThanOrEqual(margin - 0.5);
      expect(rect.top).toBeGreaterThanOrEqual(margin - 0.5);
      expect(rect.right).toBeLessThanOrEqual(width - margin + 0.5);
      expect(rect.bottom).toBeLessThanOrEqual(height - margin + 0.5);
    });

    it('limits a long menu to the viewport and scrolls it to the focused item', () => {
      const {width, height} = viewport();
      const items = Array.from({length: 80}, (_, index) => new NhContextMenuItem({title: `Project ${index + 1}`}));
      open(items);

      const rect = menu().getBoundingClientRect();
      expect(menu().style.maxWidth).toBe(`${width - 2 * margin}px`);
      expect(rect.height).toBeLessThanOrEqual(height - 2 * margin + 0.5);
      expect(menu().scrollHeight).toBeGreaterThan(menu().clientHeight);

      press('End');

      const last = menuItems()[items.length - 1];
      expect(document.activeElement).toBe(last);
      expect(menu().scrollTop).toBeGreaterThan(0);
      expect(last.getBoundingClientRect().bottom).toBeLessThanOrEqual(rect.bottom + 0.5);
    });

    it('opens below an anchor element and keeps its left edge', () => {
      invoker.style.position = 'fixed';
      invoker.style.left = '60px';
      invoker.style.top = '50px';
      const anchor = invoker.getBoundingClientRect();

      service.open(NhContextMenu.fromElement(invoker).withItems([new NhContextMenuItem({title: 'Open'})]));

      const rect = menu().getBoundingClientRect();
      expect(rect.left).toBeCloseTo(anchor.left, 0);
      expect(rect.top).toBeCloseTo(anchor.bottom, 0);
    });
  });
});
