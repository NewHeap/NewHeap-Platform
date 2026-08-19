import {distinctUntilChanged, debounceTime, takeUntil} from 'rxjs/operators';
import {Directive, OnDestroy, OnInit, input, output, HostListener} from '@angular/core';
import {NgControl} from '@angular/forms';
import {Subject, Subscription, tap} from 'rxjs';
import {Router} from "@angular/router";


@Directive({
    selector: '[ngModel][nhDebounce]',
    standalone: false
})
export class NhDebounceDirective implements OnInit, OnDestroy {
  public readonly onDebounce = output<any>();
  public readonly preDebounce = output<any>();

  public readonly debounceTime = input(500, { alias: "nhDebounce" });

  private isFirstChange = true;
  private ngUnsubscribe: Subject<void> = new Subject<void>();

  constructor(public model: NgControl) {
  }

  ngOnInit() {
    if(this.model && this.model.valueChanges) {
      this.model.valueChanges.pipe(
        takeUntil(this.ngUnsubscribe),
        debounceTime(this.debounceTime()),
        distinctUntilChanged(),)
        .subscribe(modelValue => {
          if (this.isFirstChange) {
            this.isFirstChange = false;
          } else {
            this.onDebounce.emit(modelValue);
          }
        });

      this.model.valueChanges.pipe(
        takeUntil(this.ngUnsubscribe),
        distinctUntilChanged(),)
        .subscribe(modelValue => {
          if (this.isFirstChange) {
            this.isFirstChange = false;
          } else {
            this.preDebounce.emit(modelValue);
          }
        });
    }
  }

  ngOnDestroy() {
    this.ngUnsubscribe.next();
    this.ngUnsubscribe.complete();
  }
}


@Directive({
  selector: '[nhButtonDebounce]',
  standalone: false
})
export class NhButtonDebounceDirective implements OnInit, OnDestroy {
  public readonly onDebounce = output<any>();

  public readonly debounceTime = input(500, { alias: "debounce" });

  private clicks = new Subject();
  private subscription?: Subscription;

  constructor() {
  }

  ngOnInit() {
    this.subscription = this.clicks.pipe(
      tap(_ => console.log('tap')),
      debounceTime(this.debounceTime())
    ).subscribe(e => this.onDebounce.emit(e));
  }

  @HostListener('click', ['$event'])
  clickEvent(event: any) {
    event.preventDefault();
    event.stopPropagation();
    this.clicks.next(event);
  }

  ngOnDestroy() {
    this.clicks.complete();
    this.subscription?.unsubscribe();
  }
}
