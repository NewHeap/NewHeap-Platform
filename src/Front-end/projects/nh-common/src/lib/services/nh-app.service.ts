import {
  afterNextRender,
  ApplicationRef,
  Inject,
  Injectable,
  makeStateKey,
  OnDestroy, Optional,
  PLATFORM_ID,
  StateKey, TransferState
} from "@angular/core";
import {filter, Subscription} from "rxjs";
import {DOCUMENT, isPlatformBrowser, isPlatformServer} from "@angular/common";
import {NavigationEnd, Router} from "@angular/router";

const APP_ORIGINATED_FROM_SERVER = 'APP_ORIGINATED_FROM_SERVER';

@Injectable({
  providedIn: 'root',
})
export class NhAppService implements OnDestroy {
  private $appRefIsStable: Subscription;
  private appIsStable: boolean = false;
  private afterNextRenderComplete: boolean = false;
  private firstHydrateComplete: boolean = false;
  private stateKeys: StateKey<any>[] = [];
  private browserFirstHydrateComplete$: Subscription|undefined;
  public readonly localStorage?: Storage;

  constructor(
    @Inject(DOCUMENT) private document: Document,
    @Inject(PLATFORM_ID) private platformId: Object,
    private appRef: ApplicationRef,
    private transferState: TransferState,
    private router: Router
  ) {

    this.localStorage = this.document.defaultView?.localStorage as Storage;
    this.$appRefIsStable = this.appRef.isStable.subscribe((isStable) => {
      if (isStable) {
        this.appIsStable = true;

        if(this.isPlatformServer()) {
          this.setStateTransferData(APP_ORIGINATED_FROM_SERVER, 'true');
        }
      }
    });

    afterNextRender(() => {
      this.afterNextRenderComplete = true;
    });

    if(this.isPlatformBrowser()) {
      this.browserFirstHydrateComplete$ = this.router.events
        .pipe(
          filter(event => event instanceof NavigationEnd) // Alleen NavigationEnd events
        )
        .subscribe(() => {
          if(this.isPlatformBrowser() && this.originatedFromServer() && !this.firstHydrateCompleted()) {
            this.firstHydrateComplete = true;
            this.browserFirstHydrateComplete$?.unsubscribe();
          }
        });
    }
  }

  isAppStable(): boolean {
    return this.appIsStable;
  }

  ngOnDestroy() {
    this.$appRefIsStable?.unsubscribe();
    this.browserFirstHydrateComplete$?.unsubscribe();
  }

  originatedFromServer(): boolean {
    return this.getStateTransferData(APP_ORIGINATED_FROM_SERVER) === 'true';
  }

  isPlatformServer(): boolean {
    return isPlatformServer(this.platformId);
  }

  isPlatformBrowser(): boolean {
    return isPlatformBrowser(this.platformId);
  }

  firstHydrateCompleted(): boolean {
    return this.firstHydrateComplete;
  }

  afterNextRenderCompleted(): boolean {
    return this.afterNextRenderComplete;
  }

  isPlatformBrowserInitial() {
    if(!this.originatedFromServer()) {
      return false;
    }

    return (isPlatformBrowser(this.platformId) && !this.firstHydrateCompleted() && this.afterNextRenderCompleted());
  }

  setStateTransferData(key: string, data: any) {
    let stateKey = this.stateKeys.find(x => x === key);
    if(!stateKey) {
      stateKey = makeStateKey<any>(key);
      this.stateKeys.push(stateKey);
    }

    this.transferState.set(stateKey, data);
  }

  getStateTransferData<T>(key: string): T {
    let stateKey = this.stateKeys.find(x => x === key);
    if(!stateKey) {
      stateKey = makeStateKey<any>(key);
      this.stateKeys.push(stateKey);
    }

    return this.transferState.get(stateKey, undefined as any);
  }
}
