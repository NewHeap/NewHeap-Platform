import { DatePipe } from '@angular/common';
import { Component, DestroyRef, inject, input, output } from '@angular/core';
import { NhToastrContainers } from '../services/nh-toastr-containers';
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
            (mouseenter)="setHovered(true)"
            (mouseleave)="setHovered(false)"
            (focusin)="setFocusWithin(true)"
            (focusout)="leaveFocus($event)">
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
            @if (toast().config.action; as action) {
                <div class="nh-toastr-actions">
                    <button class="nh-toastr-action" type="button" (click)="runAction($event)">{{ action.label }}</button>
                </div>
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

        .nh-toastr-actions {
            display: flex;
            justify-content: var(--nh-toastr-actions-justify, flex-end);
            margin-top: var(--nh-toastr-actions-margin-top, .6rem);
        }

        .nh-toastr.toast .nh-toastr-actions {
            margin-top: 0;
            padding: 0 var(--bs-toast-padding-x, .75rem) var(--bs-toast-padding-x, .75rem);
        }

        .nh-toastr-action {
            background: var(--nh-toastr-action-background, transparent);
            border: var(--nh-toastr-action-border-width, 1px) solid var(--nh-toastr-action-border-color, currentColor);
            border-radius: var(--nh-toastr-action-radius, .5rem);
            color: var(--nh-toastr-action-color, inherit);
            cursor: pointer;
            font: inherit;
            font-size: var(--nh-toastr-action-font-size, .875rem);
            font-weight: var(--nh-toastr-action-font-weight, 600);
            line-height: 1.25;
            min-height: var(--nh-toastr-action-min-height, 2rem);
            padding: var(--nh-toastr-action-padding, .3rem .8rem);
        }

        .nh-toastr-action:hover {
            background: var(--nh-toastr-action-hover-background, rgba(100, 116, 139, .12));
        }

        .nh-toastr-action:focus-visible {
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
    /** Emits the toast id when the `action` button is clicked. */
    public readonly actionTriggered = output<number>();

    private hovered = false;
    private focusWithin = false;
    private pausedByInteraction = false;

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

    protected runAction(event: MouseEvent): void {
        // The button is not a tap on the toast: tapToDismiss and onTap stay out of it.
        event.stopPropagation();
        this.actionTriggered.emit(this.toast().toastId);
    }

    protected setHovered(hovered: boolean): void {
        this.hovered = hovered;
        this.updatePause();
    }

    protected setFocusWithin(focusWithin: boolean): void {
        this.focusWithin = focusWithin;
        this.updatePause();
    }

    protected leaveFocus(event: FocusEvent): void {
        const toastElement = event.currentTarget as HTMLElement;
        const nextFocus = event.relatedTarget as Node | null;

        // Moving focus between the buttons of the same toast keeps it paused.
        if (nextFocus && toastElement.contains(nextFocus)) {
            return;
        }

        // Switching to another window or tab blurs the button without moving focus away from it, and
        // focus returns to it afterwards; keep the toast paused so it is still there on return.
        if (!nextFocus && !toastElement.ownerDocument.hasFocus()) {
            return;
        }

        this.setFocusWithin(false);
    }

    /**
     * The timeout pauses while the pointer is over the toast or keyboard focus is inside it, so a keyboard
     * user can reach its buttons, and resumes only once neither is the case.
     */
    private updatePause(): void {
        const shouldPause = this.hovered || this.focusWithin;
        if (shouldPause === this.pausedByInteraction) {
            return;
        }

        this.pausedByInteraction = shouldPause;
        if (shouldPause) {
            this.paused.emit(this.toast().toastId);
        } else {
            this.resumed.emit(this.toast().toastId);
        }
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
                        (resumed)="toastr.resume($event)"
                        (actionTriggered)="toastr.runAction($event)" />
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
        // While it exists, the container tells the service that toasts are visible.
        const containers = inject(NhToastrContainers);
        containers.attach();
        inject(DestroyRef).onDestroy(() => containers.detach());
    }

    protected toastsAt(position: NhToastrPosition): NhActiveToast[] {
        return this.toastr.toasts().filter(toast => toast.position === position);
    }
}
