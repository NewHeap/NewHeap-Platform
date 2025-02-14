import {Directive, ViewContainerRef} from "@angular/core";

@Directive({
    selector: '[nhModalContent]',
    standalone: false
})
export class NhModalContentDirective {
  constructor(public viewContainerRef: ViewContainerRef) {
  }
}
