import { Component, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import {
  SampleApiConnectionStateService,
  SampleUserMenuComponent
} from 'sample-project-management-common';

@Component({
  selector: 'app-workspace-layout',
  standalone: true,
  imports: [RouterLink, RouterOutlet, TranslateModule, SampleUserMenuComponent],
  templateUrl: './workspace-layout.component.html',
  styleUrl: './workspace-layout.component.scss'
})
export class WorkspaceLayoutComponent {
  readonly connectionState = inject(SampleApiConnectionStateService);
}
