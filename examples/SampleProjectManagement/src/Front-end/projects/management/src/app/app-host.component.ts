import { Component, ViewContainerRef, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { NhToastrContainerComponent } from '@newheap/nh-toastr';
import { NhModalService } from '@newheap/platform-common';

/**
 * The management application root.
 *
 * SPM-115: modals open in this root's view container, so a modal outlives the page that opened it
 * until it closes. The browser's back and forward buttons close an open modal as Escape does
 * (`NhModalOptions.closeOnBackNavigation`, on by default), with its `closed` event; navigation
 * started from code keeps it open. No modal of the sample opts out with
 * `closeOnBackNavigation: false`: the project editor and the delete confirmation belong to the page
 * they were opened on, the editor stays open while it saves, and the processing modal of the
 * platform playground is opened with `closeable: false`, which keeps it open until its work is done.
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, NhToastrContainerComponent],
  template: '<router-outlet /><nh-toastr-container />'
})
export class AppHostComponent {
  private readonly modalService = inject(NhModalService);
  private readonly viewContainerRef = inject(ViewContainerRef);

  constructor() {
    this.modalService.setViewContainerRef(this.viewContainerRef);
  }
}
