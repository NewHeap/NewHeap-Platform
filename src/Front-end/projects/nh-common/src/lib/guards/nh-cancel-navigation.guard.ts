import {
  ActivatedRouteSnapshot, CanActivateFn, CanDeactivate, GuardResult, MaybeAsync,
  RouterStateSnapshot
} from "@angular/router";
import {Observable, tap} from "rxjs";


export class NhCanCancelNavigationGuard implements CanDeactivate<ICancelNavigationComponent> {
  canDeactivate(component: ICancelNavigationComponent, currentRoute: ActivatedRouteSnapshot, currentState: RouterStateSnapshot, nextState: RouterStateSnapshot): MaybeAsync<GuardResult> {
    if (!component.canDeactivateComponent) {
      return true;
    }

    const result = component.canDeactivateComponent();
    if ((<any>result)?.then) {
      return (<Promise<boolean>>result).then(x => {
        if(x === false) {
          console.debug('Navigation cancelled by component (promise)');
        }
        return x;
      })
    }

    if ((<any>result)?.subscribe) {
      return (<Observable<boolean>>result).pipe(tap(x => {
        if(x === false) {
          console.debug('Navigation cancelled by component (observable)');
        }
      }))
    }

    if (result === false) {
      console.debug('Navigation cancelled by component');
    }
    return result;
  }

}

export interface ICancelNavigationComponent {
  canDeactivateComponent(): boolean | Promise<boolean> | Observable<boolean>;
}
