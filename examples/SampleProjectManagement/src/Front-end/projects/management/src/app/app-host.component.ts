import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { NhToastrContainerComponent } from '@newheap/nh-toastr';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, NhToastrContainerComponent],
  template: '<router-outlet /><nh-toastr-container />'
})
export class AppHostComponent {}
