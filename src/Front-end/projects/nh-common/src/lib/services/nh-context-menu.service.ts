import {Inject, Injectable, OnDestroy, PLATFORM_ID, Renderer2, RendererFactory2} from "@angular/core";
import {NhAppService} from "nh-common";

export class NhContextMenuItem {
  type: 'item'|'divider' = 'item';
  title: string = '';
  onClick: (event: any) => Promise<void> = () => Promise.resolve();

  public constructor(init?: Partial<NhContextMenuItem>) {
    Object.assign(this, init);
  }
}

// Create typedef for event to make sure it has clientX and clientY
export type NhContextEventWithCoordinates = { clientX: number; clientY: number};

export class NhContextMenu {
  x: number = 0;
  y: number = 0;
  items: NhContextMenuItem[] = [];

  static fromEvent(event?: NhContextEventWithCoordinates): NhContextMenu {
    const contextMenu = new NhContextMenu();
    if(event) {
      contextMenu.x = event.clientX;
      contextMenu.y = event.clientY;
    }

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

@Injectable({
  providedIn: 'root'
})
export class NhContextMenuService implements OnDestroy {
  public readonly config: NhContextMenuServiceConfig = new NhContextMenuServiceConfig();
  private renderer: Renderer2;
  private contextMenuElement: HTMLElement|undefined;
  private unlistenHandles: (() => void)[] = [];
  private globalListenCloseHandles: (() => void)[] = [];

  constructor(
    @Inject(PLATFORM_ID) private platformId: Object,
    private readonly appService: NhAppService,
    private readonly rendererFactory: RendererFactory2
  ) {
    this.renderer = this.rendererFactory.createRenderer(null, null);

    if(!this.appService.isPlatformServer()) {
      this.globalListenCloseHandles.push(this.renderer.listen('window', 'resize', (event) => {
        this.handleClose(event, true);
      }));

      this.globalListenCloseHandles.push(this.renderer.listen('window', 'scroll', (event) => {
        this.handleClose(event, true);
      }));

      this.globalListenCloseHandles.push(this.renderer.listen('document', 'click', (event) => {
        this.handleClose(event);
      }));

      this.globalListenCloseHandles.push(this.renderer.listen('document', 'keydown', (event) => {
        if(event?.key === "Escape") {
          this.handleClose(event);
        }
      }));
    }
  }

  ngOnDestroy() {
    this.cleanupContextMenu();
    if(this.globalListenCloseHandles) {
      for(const unlistenHandle of this.globalListenCloseHandles) {
        unlistenHandle();
      }
    }
  }

  private handleClose(event: any, force: boolean = false) {
    if(!this.isOpen() || !event) {
      return;
    }

    const targetElement = event.target as HTMLElement;
    if(!force && targetElement && targetElement.classList.contains('nh-context-menu-item')) {
      return;
    }

    this.cleanupContextMenu();
  }

  private isOpen() {
    return this.contextMenuElement !== undefined;
  }

  private getContainerElement(): HTMLElement {
    return document.getElementsByTagName('body')[0];
  }

  private cleanupContextMenu() {
    for(const unlistenHandle of this.unlistenHandles) {
      unlistenHandle();
    }

    this.unlistenHandles = [];

    if(this.contextMenuElement) {
      this.renderer.removeChild(this.getContainerElement(), this.contextMenuElement);
      this.contextMenuElement = undefined;
    }
  }

  open(contextMenu: NhContextMenu) {

    if(this.appService.isPlatformServer()) {
      return;
    }

    this.cleanupContextMenu();

    if(!contextMenu.items.any()) {
      return;
    }

    const containerElement = this.getContainerElement();
    this.contextMenuElement = this.renderer.createElement('ul') as HTMLElement;

    this.renderer.addClass(this.contextMenuElement, 'nh-context-menu');
    this.renderer.setStyle(this.contextMenuElement, 'top', contextMenu.y + 'px');
    this.renderer.setStyle(this.contextMenuElement, 'left', contextMenu.x + 'px');
    this.renderer.setStyle(this.contextMenuElement, 'position', 'absolute');
    this.renderer.setStyle(this.contextMenuElement, 'z-index', this.config.zIndex.toString());

    for(const item of contextMenu.items) {
      this.addMenuItem(this.contextMenuElement, item);
    }

    this.renderer.appendChild(containerElement, this.contextMenuElement);
  }

  close() {
    this.cleanupContextMenu();
  }

  private addMenuItem(contextMenuElement: HTMLElement, menuItem: NhContextMenuItem) {
    const menuItemElement = this.renderer.createElement('li') as HTMLElement;
    this.renderer.addClass(menuItemElement, 'nh-context-menu-item');

    if(menuItem.type === 'divider') {
      this.renderer.addClass(menuItemElement, 'divider');
    } else if (menuItem.type === 'item') {
      menuItemElement.innerHTML = menuItem.title;

      const unlistenHandle = this.renderer.listen(menuItemElement, 'click', (event) => {
        event.preventDefault();
        event.stopPropagation();

        if(menuItem.onClick) {
          menuItem.onClick(event).then(() => {
            this.cleanupContextMenu();
          });
        }
      });

      this.unlistenHandles.push(unlistenHandle);
    } else {

    }

    this.renderer.appendChild(contextMenuElement, menuItemElement);
  }
}
