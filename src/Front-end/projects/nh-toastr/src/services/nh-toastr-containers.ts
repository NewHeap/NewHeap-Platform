import { Injectable, InjectionToken, isDevMode } from '@angular/core';

/**
 * Whether toasts warn in the console when no toast container is rendered: in development mode only.
 * Internal to the package; not part of its public API.
 */
export const NH_TOASTR_DEVELOPMENT_MODE = new InjectionToken<boolean>('NH_TOASTR_DEVELOPMENT_MODE', {
    providedIn: 'root',
    factory: () => isDevMode()
});

/**
 * The toast containers the application renders now. Every `NhToastrContainerComponent` attaches itself
 * while it exists, so `NhToastrService` can tell that a toast would not be visible. Internal to the
 * package; not part of its public API.
 */
@Injectable({ providedIn: 'root' })
export class NhToastrContainers {
    private rendered = 0;

    public get anyRendered(): boolean {
        return this.rendered > 0;
    }

    public attach(): void {
        this.rendered++;
    }

    public detach(): void {
        this.rendered = Math.max(0, this.rendered - 1);
    }
}
