import {Inject, Injectable, makeStateKey, PLATFORM_ID, StateKey, TransferState} from '@angular/core';
import {isPlatformServer} from "@angular/common";
import {NhCommonModuleConfig} from "../models/config.models";
@Injectable({
  providedIn: 'root',
})
export class NhServerService {
  private static readonly DID_INIT_BY_SERVER_STATE_KEY: StateKey<boolean> = makeStateKey<boolean>('Nh-DID_INIT_BY_SERVER');

  constructor(
    @Inject(PLATFORM_ID) private platformId: Object,
    private moduleConfig: NhCommonModuleConfig,
    private transferState: TransferState
  ) {
    if(isPlatformServer(this.platformId)) {
      this.transferState.set(NhServerService.DID_INIT_BY_SERVER_STATE_KEY, true);
    }
  }

  public didInitByServer(): boolean {
    return !isPlatformServer(this.platformId)
      && this.transferState.hasKey(NhServerService.DID_INIT_BY_SERVER_STATE_KEY)
      && this.transferState.get(NhServerService.DID_INIT_BY_SERVER_STATE_KEY, false);
  }
}
