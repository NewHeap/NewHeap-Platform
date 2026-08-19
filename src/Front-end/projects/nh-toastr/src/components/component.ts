import { DatePipe } from '@angular/common';
import { Component, input, output } from '@angular/core';
import { NhActiveToast, NhToastrPosition, NhToastrService, NH_TOASTR_POSITIONS } from '../services/nh-toastr.service';

@Component({
    selector: 'nh-toastr',
    standalone: true,
    imports: [DatePipe],
    template: `
        <div
            [class]="toastClasses()"
            [attr.role]="toast().toastType === 'error' ? 'alert' : 'status'"
            (click)="tapped.emit(toast().toastId)"
            (mouseenter)="paused.emit(toast().toastId)"
            (mouseleave)="resumed.emit(toast().toastId)">
            @if (toast().title || toast().config.closeButton) {
                <div class="toast-header">
                    <strong [class]="titleClasses()" [attr.aria-label]="toast().title">{{ toast().title }}</strong>
                    <small class="text-muted">{{ toast().createdAt | date:'HH:mm' }}</small>
                    @if (toast().config.closeButton) {
                        <button class="nh-toastr-close ms-2 mb-1 close" type="button" aria-label="Close" (click)="close($event)">&times;</button>
                    }
                </div>
            }
            @if (toast().config.enableHtml) {
                <div class="toast-body">
                    <div role="alert" [class]="messageClasses()" [innerHTML]="toast().message"></div>
                </div>
            } @else {
                <div class="toast-body" [class]="messageClasses()" [attr.aria-label]="toast().message">{{ toast().message }}</div>
            }
        </div>
    `,
    styles: [`
        :host {
            display: block;
            pointer-events: auto;
        }

        .nh-toastr:not(.toast) {
            background: var(--nh-toastr-background, var(--nh-surface, #fff));
            border: var(--nh-toastr-border-width, 1px) solid var(--nh-toastr-border-color, var(--nh-border-soft, #e2e8f0));
            border-left: var(--nh-toastr-accent-width, 4px) solid var(--nh-toastr-accent, var(--nh-toastr-type-accent, #64748b));
            border-radius: var(--nh-toastr-radius, .75rem);
            box-shadow: var(--nh-toastr-shadow, 0 14px 34px rgba(15, 23, 42, .14));
            box-sizing: border-box;
            color: var(--nh-toastr-color, var(--nh-text, #253247));
            cursor: var(--nh-toastr-cursor, pointer);
            font: var(--nh-toastr-font, inherit);
            max-width: var(--nh-toastr-max-width, calc(100vw - 2rem));
            min-width: var(--nh-toastr-min-width, 18rem);
            padding: var(--nh-toastr-padding, .85rem 2.5rem .85rem 1rem);
            position: relative;
            width: var(--nh-toastr-width, 24rem);
        }

        .nh-toastr-success { --nh-toastr-type-accent: var(--nh-toastr-success-accent, #198754); }
        .nh-toastr-error { --nh-toastr-type-accent: var(--nh-toastr-error-accent, #dc3545); }
        .nh-toastr-warning { --nh-toastr-type-accent: var(--nh-toastr-warning-accent, #d98b00); }
        .nh-toastr-info { --nh-toastr-type-accent: var(--nh-toastr-info-accent, #0d6efd); }

        .nh-toastr-leaving {
            animation: nh-toastr-leave 160ms ease-in forwards;
            pointer-events: none;
        }

        @keyframes nh-toastr-leave {
            to {
                opacity: 0;
                transform: translateY(.35rem);
            }
        }

        .nh-toastr:not(.toast) .nh-toastr-title {
            color: var(--nh-toastr-title-color, inherit);
            font-size: var(--nh-toastr-title-font-size, 1rem);
            font-weight: var(--nh-toastr-title-font-weight, 700);
            margin-bottom: var(--nh-toastr-title-margin-bottom, .25rem);
        }

        .nh-toastr:not(.toast) .nh-toastr-message {
            color: var(--nh-toastr-message-color, inherit);
            line-height: var(--nh-toastr-message-line-height, 1.4);
            overflow-wrap: anywhere;
        }

        .nh-toastr:not(.toast) .nh-toastr-close {
            background: transparent;
            border: 0;
            color: var(--nh-toastr-close-color, currentColor);
            cursor: pointer;
            font-size: var(--nh-toastr-close-font-size, 1.5rem);
            line-height: 1;
            padding: 0;
            position: absolute;
            right: var(--nh-toastr-close-right, .8rem);
            top: var(--nh-toastr-close-top, .7rem);
        }

        .nh-toastr:not(.toast) .nh-toastr-close:focus-visible {
            outline: 2px solid var(--nh-toastr-focus-color, var(--nh-toastr-accent, #0d6efd));
            outline-offset: 2px;
        }
    `]
})
export class NhToastrComponent {
    public readonly toast = input.required<NhActiveToast>();
    public readonly dismissed = output<number>();
    public readonly tapped = output<number>();
    public readonly paused = output<number>();
    public readonly resumed = output<number>();

    protected toastClasses(): string {
        const toast = this.toast();
        return `nh-toastr nh-toastr-${toast.toastType} toast-${toast.toastType} ${toast.state === 'leaving' ? 'nh-toastr-leaving' : ''} ${toast.config.toastClass ?? ''}`;
    }

    protected titleClasses(): string {
        return `nh-toastr-title ${this.toast().config.titleClass ?? ''}`;
    }

    protected messageClasses(): string {
        return `nh-toastr-message ${this.toast().config.messageClass ?? ''}`;
    }

    protected close(event: MouseEvent): void {
        event.stopPropagation();
        this.dismissed.emit(this.toast().toastId);
    }
}

@Component({
    selector: 'nh-toastr-container',
    standalone: true,
    imports: [NhToastrComponent],
    template: `
        @for (position of positions; track position) {
            <div class="nh-toastr-stack" [attr.data-position]="position">
                @for (toast of toastsAt(position); track toast.toastId) {
                    <nh-toastr
                        [toast]="toast"
                        (dismissed)="toastr.remove($event)"
                        (tapped)="toastr.tap($event)"
                        (paused)="toastr.pause($event)"
                        (resumed)="toastr.resume($event)" />
                }
            </div>
        }
    `,
    styles: [`
        .nh-toastr-stack {
            display: flex;
            flex-direction: column;
            gap: var(--nh-toastr-gap, .75rem);
            pointer-events: none;
            position: fixed;
            z-index: var(--nh-toastr-z-index, 3000);
        }

        .nh-toastr-stack[data-position='toast-top-right'] { right: var(--nh-toastr-offset-x, 1rem); top: var(--nh-toastr-offset-y, 1rem); }
        .nh-toastr-stack[data-position='toast-top-left'] { left: var(--nh-toastr-offset-x, 1rem); top: var(--nh-toastr-offset-y, 1rem); }
        .nh-toastr-stack[data-position='toast-bottom-right'] { bottom: var(--nh-toastr-offset-y, 1rem); right: var(--nh-toastr-offset-x, 1rem); }
        .nh-toastr-stack[data-position='toast-bottom-left'] { bottom: var(--nh-toastr-offset-y, 1rem); left: var(--nh-toastr-offset-x, 1rem); }
        .nh-toastr-stack[data-position='toast-top-center'] { left: 50%; top: var(--nh-toastr-offset-y, 1rem); transform: translateX(-50%); }
        .nh-toastr-stack[data-position='toast-bottom-center'] { bottom: var(--nh-toastr-offset-y, 1rem); left: 50%; transform: translateX(-50%); }
        .nh-toastr-stack[data-position='bs-toast-container'] {
            gap: 0;
            overflow: hidden;
            padding: 2rem 1.5rem;
            right: 0;
            top: 0;
            z-index: 999;
        }
        .nh-toastr-stack[data-position='toast-top-full-width'] { left: var(--nh-toastr-offset-x, 1rem); right: var(--nh-toastr-offset-x, 1rem); top: var(--nh-toastr-offset-y, 1rem); }
        .nh-toastr-stack[data-position='toast-bottom-full-width'] { bottom: var(--nh-toastr-offset-y, 1rem); left: var(--nh-toastr-offset-x, 1rem); right: var(--nh-toastr-offset-x, 1rem); }

        .nh-toastr-stack[data-position$='full-width'] nh-toastr {
            width: 100%;
        }
    `]
})
export class NhToastrContainerComponent {
    public readonly positions: readonly NhToastrPosition[] = NH_TOASTR_POSITIONS;

    public constructor(public readonly toastr: NhToastrService) {
    }

    protected toastsAt(position: NhToastrPosition): NhActiveToast[] {
        return this.toastr.toasts().filter(toast => toast.position === position);
    }
}
