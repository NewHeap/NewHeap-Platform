import {
  Component,
  inject,
  OnDestroy,
  OnInit,
  Output, Type,
} from "@angular/core";
import {INhModalComponent, NhModalComponentRef, NhModalService} from "../../services/nh-modal.service";
import {BaseNhAuthService, NhAuthService} from "../../services/nh-auth.service";
import {INhAuthorization, NhAuthorization} from "../../models/auth.models";
import {NhMutateBaseTypeComponent} from "../nh-mutate-base-component/component";

@Component({
    selector: 'nh-modal-mutate-base-type-component',
    template: ``,
    standalone: false
})
export abstract class NhModalMutateBaseTypeComponent<TFormData, TResult, TAuthorization extends INhAuthorization, TAuthService extends BaseNhAuthService<TAuthorization>>
  extends NhMutateBaseTypeComponent<TFormData, TResult, TAuthorization, TAuthService>
  implements
    OnInit,
    OnDestroy,
    INhModalComponent<NhModalMutateBaseTypeComponent<TFormData, TResult, TAuthorization, TAuthService>>
{
  protected modalComponentRef: NhModalComponentRef<NhModalMutateBaseTypeComponent<TFormData, TResult, TAuthorization, TAuthService>>|undefined;
  protected modalService: NhModalService = inject(NhModalService);

  setModalComponentRef(ref: NhModalComponentRef<NhModalMutateBaseTypeComponent<TFormData, TResult, TAuthorization, TAuthService>>): void {
    this.modalComponentRef = ref;
  }

  protected constructor(
    authServiceType: Type<TAuthService>
  ) {
    super(authServiceType);
  }

  /**
   * Escape on the topmost modal shell, when it can close: closes through `closeDialog`, so an
   * override of `closeDialog` also runs for Escape.
   */
  onModalEscape(): void {
    this.closeDialog({});
  }

  /**
   * @deprecated The modal shell handles Escape on keydown for the topmost modal that can close and
   * calls `onModalEscape`; this method no longer listens to the document. Override `onModalEscape`.
   */
  onKeyUp(event: KeyboardEvent) {
    if (event.key === 'Escape') {
      event.stopImmediatePropagation();
      this.closeDialog({});
    }
  }

  closeDialog(event?: any) {
    this.modalComponentRef?.close();
  }
}

@Component({
  selector: 'nh-modal-mutate-base-component',
  template: ``,
  standalone: false
})
export abstract class NhModalMutateBaseComponent<TFormData, TResult> extends NhModalMutateBaseTypeComponent<TFormData, TResult, NhAuthorization, NhAuthService> {
  constructor() {
    super(NhAuthService);
  }
}
