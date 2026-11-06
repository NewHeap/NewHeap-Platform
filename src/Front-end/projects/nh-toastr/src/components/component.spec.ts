import { ComponentFixture, fakeAsync, flush, TestBed, tick } from '@angular/core/testing';
import { TranslateService } from '@ngx-translate/core';

import { NH_TOASTR_DEVELOPMENT_MODE } from '../services/nh-toastr-containers';
import { NhToastrService } from '../services/nh-toastr.service';
import { NhToastrContainerComponent } from './component';

describe('NhToastrComponent action button', () => {
    let fixture: ComponentFixture<NhToastrContainerComponent>;
    let toastr: NhToastrService;

    beforeEach(() => {
        TestBed.configureTestingModule({
            providers: [
                { provide: TranslateService, useValue: { instant: (key: string) => key } },
                { provide: NH_TOASTR_DEVELOPMENT_MODE, useValue: false }
            ]
        });

        fixture = TestBed.createComponent(NhToastrContainerComponent);
        toastr = TestBed.inject(NhToastrService);
    });

    function render(): void {
        fixture.detectChanges();
    }

    function toastElement(): HTMLElement {
        return fixture.nativeElement.querySelector('.nh-toastr') as HTMLElement;
    }

    function actionButton(): HTMLButtonElement {
        return fixture.nativeElement.querySelector('button.nh-toastr-action') as HTMLButtonElement;
    }

    function closeButton(): HTMLButtonElement {
        return fixture.nativeElement.querySelector('button.nh-toastr-close') as HTMLButtonElement;
    }

    function focusIn(target: HTMLElement): void {
        target.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    }

    function focusOut(target: HTMLElement, nextFocus: HTMLElement | null): void {
        target.dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: nextFocus }));
    }

    function state(): string | undefined {
        return toastr.toasts()[0]?.state;
    }

    it('renders a real button with the action label, and none for a toast without an action', fakeAsync(() => {
        toastr.info('Plain');
        toastr.success('Status changed', undefined, { action: { label: 'Undo' } });
        render();

        const buttons = fixture.nativeElement.querySelectorAll('button.nh-toastr-action') as NodeListOf<HTMLButtonElement>;
        expect(buttons.length).toBe(1);
        expect(buttons[0].type).toBe('button');
        expect(buttons[0].textContent?.trim()).toBe('Undo');
        expect(buttons[0].closest('.nh-toastr')?.textContent).toContain('Status changed');

        flush();
    }));

    it('emits the action value, dismisses the toast and does not count as a tap', fakeAsync(() => {
        const toast = toastr.success('Status changed', undefined, {
            action: { label: 'Undo', value: { previousStatus: 'draft' } }
        })!;
        const actions: unknown[] = [];
        let taps = 0;
        let hidden = false;
        toast.onAction.subscribe(value => actions.push(value));
        toast.onTap.subscribe(() => taps++);
        toast.onHidden.subscribe(() => hidden = true);
        render();

        actionButton().click();
        render();

        expect(actions).toEqual([{ previousStatus: 'draft' }]);
        expect(taps).toBe(0);
        expect(state()).toBe('leaving');

        tick(200);
        render();

        expect(hidden).toBeTrue();
        expect(toastr.toasts().length).toBe(0);
        expect(fixture.nativeElement.querySelector('.nh-toastr')).toBeNull();
    }));

    it('keeps the toast open when the action sets dismiss to false', fakeAsync(() => {
        const toast = toastr.info('Export ready', undefined, {
            disableTimeOut: true,
            action: { label: 'Copy link', value: 'copy', dismiss: false }
        })!;
        const actions: unknown[] = [];
        toast.onAction.subscribe(value => actions.push(value));
        render();

        actionButton().click();
        actionButton().click();
        render();

        expect(actions).toEqual(['copy', 'copy']);
        expect(state()).toBe('active');
        expect(actionButton()).not.toBeNull();

        toastr.clear();
        flush();
    }));

    it('pauses the timeout while keyboard focus is inside the toast and resumes once it leaves', fakeAsync(() => {
        toastr.success('Status changed', undefined, {
            timeOut: 1000,
            extendedTimeOut: 500,
            closeButton: true,
            action: { label: 'Undo' }
        });
        render();

        focusIn(actionButton());
        tick(5000);
        expect(state()).toBe('active');

        // Moving focus between the buttons of the same toast keeps it paused.
        focusOut(actionButton(), closeButton());
        focusIn(closeButton());
        tick(5000);
        expect(state()).toBe('active');

        focusOut(closeButton(), document.body);
        tick(499);
        expect(state()).toBe('active');

        tick(1);
        expect(state()).toBe('leaving');

        flush();
    }));

    it('stays paused while either the pointer or keyboard focus is on the toast', fakeAsync(() => {
        toastr.success('Status changed', undefined, {
            timeOut: 1000,
            extendedTimeOut: 500,
            action: { label: 'Undo' }
        });
        render();
        spyOn(document, 'hasFocus').and.returnValue(true);
        const pause = spyOn(toastr, 'pause').and.callThrough();
        const resume = spyOn(toastr, 'resume').and.callThrough();

        toastElement().dispatchEvent(new MouseEvent('mouseenter'));
        focusIn(actionButton());
        toastElement().dispatchEvent(new MouseEvent('mouseleave'));
        tick(5000);

        expect(state()).toBe('active');
        expect(pause).toHaveBeenCalledTimes(1);
        expect(resume).not.toHaveBeenCalled();

        focusOut(actionButton(), null);
        expect(resume).toHaveBeenCalledTimes(1);
        tick(500);
        expect(state()).toBe('leaving');

        flush();
    }));

    it('stays paused while the window is in the background with focus on the toast button', fakeAsync(() => {
        toastr.success('Status changed', undefined, {
            timeOut: 1000,
            extendedTimeOut: 500,
            action: { label: 'Undo' }
        });
        render();
        const hasFocus = spyOn(document, 'hasFocus').and.returnValue(false);

        focusIn(actionButton());
        // Another window or tab takes focus: the button blurs, but stays the focused element of the page.
        focusOut(actionButton(), null);
        tick(5000);
        expect(state()).toBe('active');

        // Back in the window, focus returns to the button and then moves elsewhere on the page.
        hasFocus.and.returnValue(true);
        focusIn(actionButton());
        focusOut(actionButton(), null);
        tick(500);
        expect(state()).toBe('leaving');

        flush();
    }));

    it('ignores runAction for a toast without an action or one that is leaving', fakeAsync(() => {
        const plain = toastr.info('Plain', undefined, { disableTimeOut: true })!;
        const withAction = toastr.info('Undoable', undefined, { disableTimeOut: true, action: { label: 'Undo', value: 1 } })!;
        const plainActions: unknown[] = [];
        const actions: unknown[] = [];
        plain.onAction.subscribe(value => plainActions.push(value));
        withAction.onAction.subscribe(value => actions.push(value));

        toastr.runAction(plain.toastId);
        toastr.remove(withAction.toastId);
        toastr.runAction(withAction.toastId);

        expect(plainActions).toEqual([]);
        expect(actions).toEqual([]);
        expect(toastr.toasts().find(toast => toast.toastId === plain.toastId)?.state).toBe('active');

        toastr.clear();
        flush();
    }));
});
