import {DOCUMENT} from "@angular/common";
import {ErrorHandler, Inject, Injectable, Injector, OnDestroy, PLATFORM_ID, Renderer2, RendererFactory2, inject} from "@angular/core";
import {NhAppService} from "./nh-app.service";

/**
 * One entry of a context menu. The title is rendered as text, never as HTML, so it may safely
 * contain user data such as a project name. Use `iconClass` for an icon instead of markup.
 */
export class NhContextMenuItem {
  type: 'item'|'divider' = 'item';
  /** The text of the item. It is rendered as text: markup in it is shown literally. */
  title: string = '';
  /** CSS classes of an optional icon before the title, rendered as `<i class="..." aria-hidden="true">`. */
  iconClass?: string;
  /** A disabled item stays visible and reachable with the arrow keys, but cannot be activated. */
  disabled: boolean = false;
  /** When set, the item is a checkbox item (`role="menuitemcheckbox"`) and this is its `aria-checked` state. */
  checked?: boolean;
  /** Runs when the item is activated. The menu closes once the returned promise settles, also when it rejects. */
  onClick: (event: any) => Promise<void> = () => Promise.resolve();

  public constructor(init?: Partial<NhContextMenuItem>) {
    Object.assign(this, init);
  }
}

// Create typedef for event to make sure it has clientX and clientY
export type NhContextEventWithCoordinates = { clientX: number; clientY: number};

/** A viewport rectangle the menu opens next to, such as the bounding box of the button that opened it. */
export type NhContextMenuAnchor = { left: number; top: number; right: number; bottom: number };

export class NhContextMenu {
  /** Viewport x coordinate (as `MouseEvent.clientX`) of the menu's preferred top-left corner. */
  x: number = 0;
  /** Viewport y coordinate (as `MouseEvent.clientY`) of the menu's preferred top-left corner. */
  y: number = 0;
  /** When set, the menu opens below this rectangle and flips above or to its right edge when it does not fit. */
  anchor?: NhContextMenuAnchor;
  items: NhContextMenuItem[] = [];

  static fromEvent(event?: NhContextEventWithCoordinates): NhContextMenu {
    const contextMenu = new NhContextMenu();
    if(event) {
      contextMenu.x = event.clientX;
      contextMenu.y = event.clientY;
    }

    return contextMenu;
  }

  /** Anchors the menu to an element, such as a button that opens the menu with the keyboard. */
  static fromElement(element: Element): NhContextMenu {
    const rect = element.getBoundingClientRect();
    const contextMenu = new NhContextMenu();
    contextMenu.x = rect.left;
    contextMenu.y = rect.bottom;
    contextMenu.anchor = {left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom};

    return contextMenu;
  }

  withItems(items: NhContextMenuItem[]): NhContextMenu {
    this.items = items;
    return this;
  }

  public constructor(init?: Partial<NhContextMenu>) {
    Object.assign(this, init);
  }
}

export class NhContextMenuServiceConfig {
  zIndex: number = 8;
  public constructor(init?: Partial<NhContextMenuServiceConfig>) {
    Object.assign(this, init);
  }
}

/** A focusable menu item and the element that renders it. */
type NhContextMenuEntry = { item: NhContextMenuItem; element: HTMLElement };

/** The space the menu keeps from the edges of the viewport, in pixels. */
const viewportMargin = 8;

/**
 * Opens one context menu at a time as a WAI-ARIA menu: focus moves into it, the arrow keys, Home
 * and End move between items, Enter and Space activate, and Escape or Tab close it and return focus
 * to the element that had it before. The menu is fixed to the viewport and kept inside it.
 */
@Injectable({
  providedIn: 'root'
})
export class NhContextMenuService implements OnDestroy {
  public readonly config: NhContextMenuServiceConfig = new NhContextMenuServiceConfig();
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private renderer: Renderer2;
  private contextMenuElement: HTMLElement|undefined;
  private entries: NhContextMenuEntry[] = [];
  private restoreFocusTo: HTMLElement|null = null;
  private activating = false;
  private unlistenHandles: (() => void)[] = [];

  constructor(
    @Inject(PLATFORM_ID) private platformId: Object,
    private readonly appService: NhAppService,
    private readonly rendererFactory: RendererFactory2
  ) {
    this.renderer = this.rendererFactory.createRenderer(null, null);
  }

  ngOnDestroy() {
    this.cleanupContextMenu(false);
  }

  open(contextMenu: NhContextMenu) {
    if(this.appService.isPlatformServer()) {
      return;
    }

    if(!contextMenu.items || contextMenu.items.length === 0) {
      this.close();
      return;
    }

    const restoreFocusTo = this.focusToRestore();
    this.cleanupContextMenu(false);

    const menuElement = this.createMenuElement(contextMenu.items);
    this.contextMenuElement = menuElement;
    this.restoreFocusTo = restoreFocusTo;

    this.renderer.appendChild(this.document.body, menuElement);
    this.placeInViewport(menuElement, contextMenu);
    this.listenWhileOpen(menuElement);

    this.focusEntry(0);
  }

  /** Closes the open menu, if any, and returns focus to the element that had it before the menu opened. */
  close() {
    this.cleanupContextMenu(true);
  }

  private createMenuElement(items: NhContextMenuItem[]): HTMLElement {
    const menuElement = this.renderer.createElement('ul') as HTMLElement;
    this.renderer.addClass(menuElement, 'nh-context-menu');
    this.renderer.setAttribute(menuElement, 'role', 'menu');
    this.renderer.setAttribute(menuElement, 'tabindex', '-1');
    // Placement depends on these: a margin would shift the measured box and max sizes include the border.
    this.renderer.setStyle(menuElement, 'position', 'fixed');
    this.renderer.setStyle(menuElement, 'left', '0px');
    this.renderer.setStyle(menuElement, 'top', '0px');
    this.renderer.setStyle(menuElement, 'margin', '0px');
    this.renderer.setStyle(menuElement, 'box-sizing', 'border-box');
    this.renderer.setStyle(menuElement, 'overflow-y', 'auto');
    this.renderer.setStyle(menuElement, 'z-index', this.config.zIndex.toString());

    for(const item of items) {
      this.addMenuItem(menuElement, item);
    }

    return menuElement;
  }

  private addMenuItem(menuElement: HTMLElement, menuItem: NhContextMenuItem) {
    const menuItemElement = this.renderer.createElement('li') as HTMLElement;
    this.renderer.addClass(menuItemElement, 'nh-context-menu-item');

    if(menuItem.type === 'divider') {
      this.renderer.addClass(menuItemElement, 'divider');
      this.renderer.setAttribute(menuItemElement, 'role', 'separator');
      this.renderer.appendChild(menuElement, menuItemElement);
      return;
    }

    if(menuItem.type !== 'item') {
      return;
    }

    const checkable = menuItem.checked !== undefined && menuItem.checked !== null;
    this.renderer.setAttribute(menuItemElement, 'role', checkable ? 'menuitemcheckbox' : 'menuitem');
    this.renderer.setAttribute(menuItemElement, 'tabindex', '-1');

    if(checkable) {
      this.renderer.setAttribute(menuItemElement, 'aria-checked', menuItem.checked ? 'true' : 'false');
      this.renderer.addClass(menuItemElement, 'checkable');
    }

    if(menuItem.disabled) {
      this.renderer.setAttribute(menuItemElement, 'aria-disabled', 'true');
      this.renderer.addClass(menuItemElement, 'disabled');
    }

    if(menuItem.iconClass) {
      const iconElement = this.renderer.createElement('i') as HTMLElement;
      this.renderer.setAttribute(iconElement, 'class', `nh-context-menu-item-icon ${menuItem.iconClass}`);
      this.renderer.setAttribute(iconElement, 'aria-hidden', 'true');
      this.renderer.appendChild(menuItemElement, iconElement);
    }

    const titleElement = this.renderer.createElement('span') as HTMLElement;
    this.renderer.addClass(titleElement, 'nh-context-menu-item-title');
    this.renderer.appendChild(titleElement, this.renderer.createText(menuItem.title ?? ''));
    this.renderer.appendChild(menuItemElement, titleElement);

    const entry: NhContextMenuEntry = {item: menuItem, element: menuItemElement};
    this.entries.push(entry);

    this.listen(menuItemElement, 'click', (event: MouseEvent) => {
      event.preventDefault();
      event.stopPropagation();
      void this.activate(entry, event, menuElement);
    });

    this.renderer.appendChild(menuElement, menuItemElement);
  }

  /**
   * Fixes the menu to the viewport, measures it at the top-left corner and moves it to the requested
   * position, flipped left or up when it would overflow and clamped inside the viewport. A menu
   * larger than the viewport scrolls.
   */
  private placeInViewport(menuElement: HTMLElement, contextMenu: NhContextMenu) {
    const viewportWidth = this.document.documentElement.clientWidth || this.document.defaultView?.innerWidth || 0;
    const viewportHeight = this.document.documentElement.clientHeight || this.document.defaultView?.innerHeight || 0;
    this.renderer.setStyle(menuElement, 'max-width', `${Math.max(0, viewportWidth - 2 * viewportMargin)}px`);
    this.renderer.setStyle(menuElement, 'max-height', `${Math.max(0, viewportHeight - 2 * viewportMargin)}px`);

    const anchor = contextMenu.anchor ?? {left: contextMenu.x, top: contextMenu.y, right: contextMenu.x, bottom: contextMenu.y};
    const size = menuElement.getBoundingClientRect();
    const left = NhContextMenuService.placeOnAxis(anchor.left, anchor.right, size.width, viewportWidth);
    const top = NhContextMenuService.placeOnAxis(anchor.bottom, anchor.top, size.height, viewportHeight);

    this.renderer.setStyle(menuElement, 'left', `${left}px`);
    this.renderer.setStyle(menuElement, 'top', `${top}px`);
  }

  /** Starts at `preferred`, ends at `flipEdge` when that would overflow, and stays inside the viewport. */
  private static placeOnAxis(preferred: number, flipEdge: number, size: number, viewport: number): number {
    let position = preferred;
    if(position + size > viewport - viewportMargin) {
      position = flipEdge - size;
    }

    const maximum = viewport - viewportMargin - size;
    return Math.max(viewportMargin, Math.min(position, maximum));
  }

  private listenWhileOpen(menuElement: HTMLElement) {
    this.listen(menuElement, 'keydown', (event: KeyboardEvent) => {
      this.handleMenuKeydown(event, menuElement);
    });

    // A right-click on the menu itself must not open the browser's own menu on top of it.
    this.listen(menuElement, 'contextmenu', (event: MouseEvent) => {
      event.preventDefault();
    });

    // Capture phase: a pointer down elsewhere closes the menu even when that element stops propagation.
    // A pointer down precedes the click or contextmenu event that opened the menu, so it cannot close it.
    this.listen(this.document, 'pointerdown', (event: PointerEvent) => {
      if(!this.isInside(menuElement, event.target)) {
        this.cleanupContextMenu(false);
      }
    }, {capture: true});

    // Capture phase: Escape closes the menu before a modal below it sees the key.
    this.listen(this.document, 'keydown', (event: KeyboardEvent) => {
      if(event.key === 'Escape' && !event.isComposing) {
        event.preventDefault();
        this.cleanupContextMenu(true);
      }
    }, {capture: true});

    // Scroll events of inner containers do not bubble; the capture phase sees them all.
    this.listen(this.document, 'scroll', (event: Event) => {
      if(!this.isInside(menuElement, event.target)) {
        this.cleanupContextMenu(true);
      }
    }, {capture: true, passive: true});

    const view = this.document.defaultView;
    if(view) {
      this.listen(view, 'resize', () => {
        this.cleanupContextMenu(true);
      });
    }
  }

  private handleMenuKeydown(event: KeyboardEvent, menuElement: HTMLElement) {
    if(this.contextMenuElement !== menuElement || event.isComposing) {
      return;
    }

    const current = this.entries.findIndex(entry => entry.element === this.document.activeElement);

    switch(event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.focusEntry(current < 0 ? 0 : current + 1);
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.focusEntry(current < 0 ? this.entries.length - 1 : current - 1);
        break;
      case 'Home':
      case 'PageUp':
        event.preventDefault();
        this.focusEntry(0);
        break;
      case 'End':
      case 'PageDown':
        event.preventDefault();
        this.focusEntry(this.entries.length - 1);
        break;
      case 'Enter':
      case ' ':
        event.preventDefault();
        if(current >= 0) {
          void this.activate(this.entries[current], event, menuElement);
        }
        break;
      case 'Tab':
        // Focus returns to the element that opened the menu; the browser then moves on from there.
        this.cleanupContextMenu(true);
        break;
    }
  }

  /** Focuses the entry at `index`, wrapping around at both ends, and scrolls it into the menu's view. */
  private focusEntry(index: number) {
    const menuElement = this.contextMenuElement;
    if(!menuElement) {
      return;
    }

    if(this.entries.length === 0) {
      menuElement.focus({preventScroll: true});
      return;
    }

    const count = this.entries.length;
    const target = this.entries[((index % count) + count) % count].element;
    for(const entry of this.entries) {
      this.renderer.setAttribute(entry.element, 'tabindex', entry.element === target ? '0' : '-1');
    }

    target.focus({preventScroll: true});

    if(target.offsetTop < menuElement.scrollTop) {
      menuElement.scrollTop = target.offsetTop;
    } else if(target.offsetTop + target.offsetHeight > menuElement.scrollTop + menuElement.clientHeight) {
      menuElement.scrollTop = target.offsetTop + target.offsetHeight - menuElement.clientHeight;
    }
  }

  /**
   * Runs the item's action once and closes the menu when the action settles. A failing action
   * still closes the menu and its error goes to Angular's `ErrorHandler`.
   */
  private async activate(entry: NhContextMenuEntry, event: Event, menuElement: HTMLElement): Promise<void> {
    if(entry.item.disabled || this.activating || this.contextMenuElement !== menuElement) {
      return;
    }

    this.activating = true;

    try {
      if(entry.item.onClick) {
        await entry.item.onClick(event);
      }
    } catch(error) {
      this.injector.get(ErrorHandler).handleError(error);
    } finally {
      // An action that opened another menu keeps that one open.
      if(this.contextMenuElement === menuElement) {
        this.cleanupContextMenu(true);
      }
    }
  }

  /** The element to focus after the menu closes. A menu replaced from inside keeps the original one. */
  private focusToRestore(): HTMLElement|null {
    const active = this.document.activeElement;
    if(this.contextMenuElement && this.isInside(this.contextMenuElement, active)) {
      return this.restoreFocusTo;
    }

    if(active instanceof HTMLElement && active !== this.document.body) {
      return active;
    }

    return null;
  }

  private cleanupContextMenu(restoreFocus: boolean) {
    for(const unlistenHandle of this.unlistenHandles) {
      unlistenHandle();
    }

    this.unlistenHandles = [];
    this.entries = [];
    this.activating = false;

    const menuElement = this.contextMenuElement;
    const restoreFocusTo = this.restoreFocusTo;
    this.contextMenuElement = undefined;
    this.restoreFocusTo = null;

    if(!menuElement) {
      return;
    }

    // Focus goes back only while it is in the menu or nowhere, so an element the user moved to keeps it.
    const active = this.document.activeElement;
    const focusHere = !active || active === this.document.body || this.isInside(menuElement, active);

    this.renderer.removeChild(menuElement.parentNode, menuElement);

    if(restoreFocus && focusHere && restoreFocusTo && restoreFocusTo.isConnected) {
      restoreFocusTo.focus({preventScroll: true});
    }
  }

  private isInside(menuElement: HTMLElement, target: EventTarget|Element|null): boolean {
    return target instanceof Node && menuElement.contains(target);
  }

  private listen<T extends Event>(target: EventTarget, type: string, handler: (event: T) => void, options?: AddEventListenerOptions) {
    const listener = handler as EventListener;
    target.addEventListener(type, listener, options);
    this.unlistenHandles.push(() => target.removeEventListener(type, listener, options));
  }
}
