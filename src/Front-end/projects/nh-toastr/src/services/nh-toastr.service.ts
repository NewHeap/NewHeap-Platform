import { inject, Injectable, InjectionToken, Provider, signal } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { Observable, Subject } from 'rxjs';

export type NhToastrType = 'success' | 'error' | 'info' | 'warning';
export type NhToastrPosition =
    | 'toast-top-right'
    | 'toast-top-left'
    | 'toast-bottom-right'
    | 'toast-bottom-left'
    | 'toast-top-full-width'
    | 'toast-bottom-full-width'
    | 'toast-top-center'
    | 'toast-bottom-center'
    | 'bs-toast-container';

/** A compatible subset of the legacy toast configuration. */
export interface NhToastrConfig {
    disableTimeOut?: boolean | 'timeOut' | 'extendedTimeOut';
    timeOut?: number;
    extendedTimeOut?: number;
    closeButton?: boolean;
    enableHtml?: boolean;
    toastClass?: string;
    positionClass?: string;
    titleClass?: string;
    messageClass?: string;
    tapToDismiss?: boolean;
    newestOnTop?: boolean;
    preventDuplicates?: boolean;
    includeTitleDuplicates?: boolean;
}

export const NH_TOASTR_CONFIG = new InjectionToken<NhToastrConfig>('NH_TOASTR_CONFIG');

/** Configure default values for all NhToastrService instances in the application root. */
export function provideNhToastr(config: NhToastrConfig): Provider {
    return { provide: NH_TOASTR_CONFIG, useValue: config };
}

export interface NhActiveToast {
    toastId: number;
    message: string;
    title?: string;
    createdAt: Date;
    toastType: string;
    position: NhToastrPosition;
    state: 'active' | 'leaving';
    config: NhToastrConfig;
    onShown: Observable<void>;
    onHidden: Observable<void>;
    onTap: Observable<void>;
    onAction: Observable<unknown>;
}

interface ToastLifecycle {
    onShown: Subject<void>;
    onHidden: Subject<void>;
    onTap: Subject<void>;
    onAction: Subject<unknown>;
    timeout?: ReturnType<typeof setTimeout>;
    leaveTimeout?: ReturnType<typeof setTimeout>;
}

export const NH_TOASTR_POSITIONS: readonly NhToastrPosition[] = [
    'toast-top-right',
    'toast-top-left',
    'toast-bottom-right',
    'toast-bottom-left',
    'toast-top-full-width',
    'toast-bottom-full-width',
    'toast-top-center',
    'toast-bottom-center',
    'bs-toast-container'
];

const defaultToastrConfig: NhToastrConfig = {
    timeOut: 5000,
    extendedTimeOut: 1000,
    closeButton: false,
    enableHtml: false,
    toastClass: 'nh-toastr',
    positionClass: 'toast-bottom-right',
    titleClass: 'nh-toastr-title',
    messageClass: 'nh-toastr-message',
    tapToDismiss: true,
    newestOnTop: true,
    preventDuplicates: false,
    includeTitleDuplicates: false
};

@Injectable({ providedIn: 'root' })
export class NhToastrService {
    /** Application-wide defaults for newly created toasts. */
    public readonly toastrConfig: NhToastrConfig;

    public readonly toasts = signal<NhActiveToast[]>([]);
    public currentlyActive = 0;

    private readonly lifecycles = new Map<number, ToastLifecycle>();
    private readonly applicationConfig = inject(NH_TOASTR_CONFIG, { optional: true });
    private nextToastId = 0;
    private readonly leaveAnimationDuration = 160;

    public constructor(private readonly translationService: TranslateService) {
        this.toastrConfig = { ...defaultToastrConfig, ...this.applicationConfig };
    }

    /** Changes the defaults for toasts created after this call. */
    public configure(config: NhToastrConfig): void {
        Object.assign(this.toastrConfig, config);
    }

    public success(message?: string, title?: string, override?: Partial<NhToastrConfig>): NhActiveToast | null {
        return this.show(message ?? this.translationService.instant('general.success'), title, override, 'success');
    }

    public error(message?: string, title?: string, override?: Partial<NhToastrConfig>): NhActiveToast | null {
        return this.show(message ?? this.translationService.instant('general.error'), title, override, 'error');
    }

    public info(message?: string, title?: string, override?: Partial<NhToastrConfig>): NhActiveToast | null {
        return this.show(message, title, override, 'info');
    }

    public warning(message?: string, title?: string, override?: Partial<NhToastrConfig>): NhActiveToast | null {
        return this.show(message, title, override, 'warning');
    }

    public show(message?: string, title?: string, override?: Partial<NhToastrConfig>, type: string = 'info'): NhActiveToast | null {
        const config = { ...this.toastrConfig, ...override };
        const toastMessage = message ?? '';
        const duplicate = config.preventDuplicates
            ? this.findDuplicate(title, toastMessage, config.includeTitleDuplicates)
            : undefined;

        if (duplicate) {
            return duplicate;
        }

        const lifecycle: ToastLifecycle = {
            onShown: new Subject<void>(),
            onHidden: new Subject<void>(),
            onTap: new Subject<void>(),
            onAction: new Subject<unknown>()
        };
        const toastId = ++this.nextToastId;
        const toast: NhActiveToast = {
            toastId,
            message: toastMessage,
            title,
            createdAt: new Date(),
            toastType: type,
            position: this.getPosition(config.positionClass),
            state: 'active',
            config,
            onShown: lifecycle.onShown.asObservable(),
            onHidden: lifecycle.onHidden.asObservable(),
            onTap: lifecycle.onTap.asObservable(),
            onAction: lifecycle.onAction.asObservable()
        };

        this.lifecycles.set(toastId, lifecycle);
        this.toasts.update(toasts => config.newestOnTop ? [toast, ...toasts] : [...toasts, toast]);
        this.currentlyActive = this.toasts().length;
        this.scheduleDismissal(toast, config.timeOut, 'timeOut');
        queueMicrotask(() => lifecycle.onShown.next());
        return toast;
    }

    public clear(toastId?: number): void {
        if (toastId !== undefined) {
            this.remove(toastId);
            return;
        }

        this.toasts().forEach(toast => this.remove(toast.toastId));
    }

    public remove(toastId: number): boolean {
        const toast = this.toasts().find(item => item.toastId === toastId);
        if (!toast || toast.state === 'leaving') {
            return false;
        }

        const lifecycle = this.lifecycles.get(toastId);
        if (lifecycle?.timeout) {
            clearTimeout(lifecycle.timeout);
        }
        this.toasts.update(toasts => toasts.map(item => item.toastId === toastId ? { ...item, state: 'leaving' } : item));
        if (lifecycle) {
            lifecycle.leaveTimeout = setTimeout(() => this.finalizeRemove(toastId), this.leaveAnimationDuration);
        }
        return true;
    }

    public findDuplicate(title: string | undefined, message: string, includeTitle: boolean = false): NhActiveToast | undefined {
        return this.toasts().find(toast => toast.state === 'active' && toast.message === message && (!includeTitle || toast.title === title));
    }

    public tap(toastId: number): void {
        const toast = this.toasts().find(item => item.toastId === toastId);
        if (!toast || toast.state === 'leaving') {
            return;
        }

        this.lifecycles.get(toastId)?.onTap.next();
        if (toast.config.tapToDismiss) {
            this.remove(toastId);
        }
    }

    public pause(toastId: number): void {
        const lifecycle = this.lifecycles.get(toastId);
        if (lifecycle?.timeout) {
            clearTimeout(lifecycle.timeout);
            lifecycle.timeout = undefined;
        }
    }

    public resume(toastId: number): void {
        const toast = this.toasts().find(item => item.toastId === toastId);
        if (toast?.state === 'active') {
            this.scheduleDismissal(toast, toast.config.extendedTimeOut, 'extendedTimeOut');
        }
    }

    public triggerAction(toastId: number, value?: unknown): void {
        this.lifecycles.get(toastId)?.onAction.next(value);
    }

    private finalizeRemove(toastId: number): void {
        const lifecycle = this.lifecycles.get(toastId);
        if (!lifecycle) {
            return;
        }

        if (lifecycle.timeout) {
            clearTimeout(lifecycle.timeout);
        }
        lifecycle.onHidden.next();
        lifecycle.onShown.complete();
        lifecycle.onHidden.complete();
        lifecycle.onTap.complete();
        lifecycle.onAction.complete();
        this.lifecycles.delete(toastId);
        this.toasts.update(toasts => toasts.filter(item => item.toastId !== toastId));
        this.currentlyActive = this.toasts().length;
    }

    private scheduleDismissal(toast: NhActiveToast, timeout: number | undefined, timeoutType: 'timeOut' | 'extendedTimeOut'): void {
        if (!timeout || timeout < 1 || toast.config.disableTimeOut === true || toast.config.disableTimeOut === timeoutType) {
            return;
        }

        const lifecycle = this.lifecycles.get(toast.toastId);
        if (!lifecycle) {
            return;
        }

        if (lifecycle.timeout) {
            clearTimeout(lifecycle.timeout);
        }
        lifecycle.timeout = setTimeout(() => this.remove(toast.toastId), timeout);
    }

    private getPosition(positionClass: string | undefined): NhToastrPosition {
        return NH_TOASTR_POSITIONS.includes(positionClass as NhToastrPosition)
            ? positionClass as NhToastrPosition
            : 'toast-bottom-right';
    }
}
