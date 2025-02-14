import {HostListener, Injectable, NgZone} from "@angular/core";
import {BehaviorSubject} from "rxjs";

@Injectable()
export class NhInternetConnectionService {
  private connectionSubject = new BehaviorSubject<boolean>(navigator.onLine);
  public internetIsConnected = this.connectionSubject.asObservable();
  constructor(private zone: NgZone) {
    window.addEventListener("online", (event) => {
      this.zone.run(() => {
        this.connectionSubject.next(true);
      });
    });

    window.addEventListener("offline", (event) => {
      this.zone.run(() => {
        this.connectionSubject.next(false);
      });
    });
  }
}
