import {debounceTime, distinctUntilChanged, filter, map, skip, takeUntil} from 'rxjs/operators';
import {Directive, OnDestroy, OnInit, input, output, HostListener} from '@angular/core';
import {NgControl} from '@angular/forms';
import {Subject, Subscription} from 'rxjs';

/** A value of the debounced stream, marked when it is the initial value that `ngModel` wrote. */
interface NhDebounceValue {
  value: unknown;
  isInitialValue: boolean;
}

/**
 * Reports changes of an `ngModel` field: `preDebounce` at once and `onDebounce` once the value has
 * not changed for `nhDebounce` milliseconds (500 by default).
 *
 * The initial value that `ngModel` writes is not a change, so neither output reports it: load the
 * initial data of a page yourself. Later values, typed or written through `ngModel`, are reported.
 * A value that is still waiting when the field is destroyed is not reported.
 */
@Directive({
    selector: '[ngModel][nhDebounce]',
    standalone: false
})
export class NhDebounceDirective implements OnInit, OnDestroy {
  public readonly onDebounce = output<any>();
  public readonly preDebounce = output<any>();

  public readonly debounceTime = input(500, { alias: "nhDebounce" });

  private ngUnsubscribe: Subject<void> = new Subject<void>();

  constructor(public model: NgControl) {
  }

  ngOnInit() {
    const valueChanges = this.model?.valueChanges;
    if (!valueChanges) {
      return;
    }

    // ngModel writes its initial value after this hook, as the first value of valueChanges. Each
    // stream recognizes that value on its own; one flag shared by both streams was consumed by the
    // immediate stream, so the debounced stream reported the initial value. The debounced stream
    // marks the value before debouncing, so a value typed within the debounce time is still reported.
    valueChanges.pipe(
      map((value, index): NhDebounceValue => ({value, isInitialValue: index === 0})),
      debounceTime(this.debounceTime()),
      distinctUntilChanged((previous, current) => previous.value === current.value),
      filter(change => !change.isInitialValue),
      takeUntil(this.ngUnsubscribe))
      .subscribe(change => this.onDebounce.emit(change.value));

    valueChanges.pipe(
      distinctUntilChanged(),
      skip(1),
      takeUntil(this.ngUnsubscribe))
      .subscribe(value => this.preDebounce.emit(value));
  }

  ngOnDestroy() {
    // takeUntil comes last in both streams: completing the source instead would make debounceTime
    // emit its waiting value while the field is destroyed.
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
