import { ApplicationRef, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslateService } from '@ngx-translate/core';

import { NhToastrContainerComponent } from '../components/component';
import { NH_TOASTR_DEVELOPMENT_MODE } from './nh-toastr-containers';
import { NhToastrService } from './nh-toastr.service';

@Component({
    selector: 'nh-toastr-test-root',
    standalone: true,
    imports: [NhToastrContainerComponent],
    template: `@if (showContainer()) { <nh-toastr-container /> }`
})
class TestRootComponent {
    public readonly showContainer = signal(true);
}

describe('NhToastrService without a toast container', () => {
    let warn: jasmine.Spy;

    function configure(developmentMode?: boolean): void {
        TestBed.configureTestingModule({
            providers: [
                { provide: TranslateService, useValue: { instant: (key: string) => key } },
                ...(developmentMode === undefined ? [] : [{ provide: NH_TOASTR_DEVELOPMENT_MODE, useValue: developmentMode }])
            ]
        });
    }

    function render(): void {
        TestBed.inject(ApplicationRef).tick();
    }

    function containerWarnings(): unknown[][] {
        return warn.calls.allArgs().filter(([message]) => String(message).includes('no toast container is rendered'));
    }

    beforeEach(() => {
        warn = spyOn(console, 'warn');
    });

    it('warns once in development mode, naming the container and the provider', () => {
        configure();
        const toastr = TestBed.inject(NhToastrService);

        toastr.success('Saved');
        render();
        toastr.error('Failed');
        render();

        expect(containerWarnings().length).toBe(1);
        const [message] = containerWarnings()[0];
        expect(message).toContain('<nh-toastr-container>');
        expect(message).toContain('NhToastrContainerComponent');
        expect(message).toContain('provideNhToastr');
        // The toasts themselves are kept as before.
        expect(toastr.toasts().length).toBe(2);
    });

    it('does not warn while a container is rendered', () => {
        configure();
        const root = TestBed.createComponent(TestRootComponent);
        root.detectChanges();
        const toastr = TestBed.inject(NhToastrService);

        toastr.info('Visible');
        render();

        expect(containerWarnings().length).toBe(0);
    });

    it('warns once the container is gone', () => {
        configure();
        const root = TestBed.createComponent(TestRootComponent);
        root.detectChanges();
        root.componentInstance.showContainer.set(false);
        root.detectChanges();
        const toastr = TestBed.inject(NhToastrService);

        toastr.warning('Hidden');
        render();

        expect(containerWarnings().length).toBe(1);
    });

    it('never warns in production', () => {
        configure(false);
        const toastr = TestBed.inject(NhToastrService);

        toastr.success('Saved');
        render();

        expect(containerWarnings().length).toBe(0);
        expect(toastr.toasts().length).toBe(1);
    });
});
