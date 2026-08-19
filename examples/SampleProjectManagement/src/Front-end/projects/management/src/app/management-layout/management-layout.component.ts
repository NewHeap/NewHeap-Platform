import { Component, HostListener, OnDestroy, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { Subscription, filter } from 'rxjs';
import {
  SampleApiConnectionStateService,
  SampleUserMenuComponent
} from 'sample-project-management-common';

@Component({
  selector: 'app-management-layout',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, TranslateModule, SampleUserMenuComponent],
  templateUrl: './management-layout.component.html',
  styleUrl: './management-layout.component.scss'
})
export class ManagementLayoutComponent implements OnDestroy {
  private readonly router = inject(Router);
  private readonly navigationSubscription: Subscription;

  readonly connectionState = inject(SampleApiConnectionStateService);
  readonly navigationOpen = signal(false);
  readonly pageTitle = signal('Samplecatalogus');

  constructor() {
    this.syncPageTitle();
    this.navigationSubscription = this.router.events
      .pipe(filter(event => event instanceof NavigationEnd))
      .subscribe(() => {
        this.syncPageTitle();
        this.navigationOpen.set(false);
      });
  }

  ngOnDestroy(): void {
    this.navigationSubscription.unsubscribe();
  }

  toggleNavigation(): void {
    this.navigationOpen.update(open => !open);
  }

  closeNavigation(): void {
    this.navigationOpen.set(false);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closeNavigation();
  }

  private syncPageTitle(): void {
    let route = this.router.routerState.snapshot.root;
    while (route.firstChild) {
      route = route.firstChild;
    }
    this.pageTitle.set(route.title ?? 'Samplecatalogus');
  }
}
