import {
  Component,
  EventEmitter,
  HostListener,
  inject,
  Input,
  OnDestroy,
  OnInit,
  Output, Type,
  ViewChild
} from "@angular/core";
import {INhModalComponent, NhModalComponentRef, NhModalService} from "../../services/nh-modal.service";
import {BaseNhAuthService, NhAuthService} from "../../services/nh-auth.service";
import {TranslateService} from "@ngx-translate/core";
import { ToastrService } from "ngx-toastr";
import {ActivatedRoute} from "@angular/router";
import {ClaimTypes, INhAuthorization, NhAuthorization} from "../../models/auth.models";
import {
  AspMvcFormServerSideFormValidator,
  NhServerSideFormValidationService
} from "../../services/nh-server-side-form-validator.service";
import {NgForm, UntypedFormControl} from "@angular/forms";
import { NhFormHelper } from "../../util/nh-form.util";
import { NhRouterService } from "../../services/nh-router.service";
import { NhTaskResultFormValidationService } from "../../services/nh-task-result-form.validator";
import { TaskResult } from "../../models/misc.models";


export enum MutationType {
  Create = 0,
  Update = 1
}

@Component({
    selector: 'nh-mutate-base-type-component',
    template: ``,
    standalone: false
})
export abstract class NhMutateBaseTypeComponent<TFormData, TResult, TAuthorization extends INhAuthorization, TAuthService extends BaseNhAuthService<TAuthorization>>
  implements
    OnInit,
    OnDestroy
{
  protected authService: TAuthService;
  protected translateService: TranslateService = inject(TranslateService);
  protected toastrService: ToastrService = inject(ToastrService);
  protected activatedRoute: ActivatedRoute = inject(ActivatedRoute);
  protected nhRouterService: NhRouterService = inject(NhRouterService);
  protected formValidator: NhServerSideFormValidationService = inject(NhServerSideFormValidationService);
  protected taskResultFormValidator: NhTaskResultFormValidationService = inject(NhTaskResultFormValidationService);
  claimTypes = ClaimTypes;
  private _isLoading: boolean = false;
  private _isSubmitting: boolean = false;
  private _mutationType: MutationType = MutationType.Create;
  private _formData: TFormData|undefined;

  public get isLoading(): boolean {
    return this._isLoading;
  }

  protected set isLoading(value: boolean) {
    this._isLoading = value;
  }

  public get isSubmitting(): boolean {
    return this._isSubmitting;
  }

  protected set isSubmitting(value: boolean) {
    this._isSubmitting = value;
  }

  protected get isLoadingOrSubmitting(): boolean {
    return this.isLoading || this.isSubmitting;
  }

  public get mutationType(): MutationType {
    return this._mutationType;
  }

  protected set mutationType(value: MutationType) {
    this._mutationType = value;
  }

  public get formData(): TFormData|undefined {
    return this._formData;
  }

  protected set formData(value: TFormData|undefined) {
    this._formData = value;
  }

  @ViewChild(NgForm) form: any|undefined;
  @Output() created = new EventEmitter<TResult>();
  @Output() updated = new EventEmitter<TResult>();

  protected constructor(
    authServiceType: Type<TAuthService>
  ) {
    this.authService = inject(authServiceType);
  }

  abstract onNewFormData(mutationType: MutationType): Promise<TFormData>;
  abstract onSubmitCreate(event: any): Promise<TaskResult<TResult>>;
  abstract onSubmitUpdate(event: any): Promise<TaskResult<TResult>>;



  ngOnInit(): void {
  }

  ngOnDestroy(): void {
  }

  public async newFormData(mutationType: MutationType) {
    if(this.isLoadingOrSubmitting) {
      return;
    }

    try {
      if(this.form) {
        NhFormHelper.clearErrors(this.form);
        this.form.controls[''] = new UntypedFormControl(); // Add Empty control for default errors
      }
    }catch(ex) {}

    try {
      this.isLoading = true;
      this.mutationType = mutationType;
      this.formData = await this.onNewFormData(mutationType);

    } catch (ex) {
      console.error(ex);
    } finally {
      this.isLoading = false;
    }
  }

  async submit(event: any, handleAll: boolean = true) {
    if(this.isLoadingOrSubmitting) {
      return;
    }

    this.isSubmitting = true;

    try {
      if(this.form) {
        this.form.controls[''] = new UntypedFormControl(); // Add Empty control for default errors
        NhFormHelper.clearErrors(this.form);
      }

      try {
        if (this.mutationType === MutationType.Create) {
          const createResult = await this.onSubmitCreate(event);
          if(!createResult.isSuccess) {
            this.taskResultFormValidator.validate(this.form, createResult.items);
            return;
          }

          if(handleAll) {
            this.created.emit(createResult.data);
          }
        } else {
          const updateResult = await this.onSubmitUpdate(event);
          if(!updateResult.isSuccess) {
            this.taskResultFormValidator.validate(this.form, updateResult.items);
            return;
          }

          if(handleAll) {
            this.updated.emit(updateResult.data);
          }
        }

        if(handleAll) {
          this.form.reset();
          await this.newFormData(this.mutationType);
        }
      } catch (err: any) {
        console.error(err);
        if (err.error instanceof Error) {
          this.form.controls[''].setErrors({remote: [this.translateService.instant('An unknown error occurred.')]});
        } else {
          this.formValidator.validate(AspMvcFormServerSideFormValidator, this.form, err);
        }
      }
    } finally {
      this.isSubmitting = false;
    }
  }
}

@Component({
  selector: 'nh-mutate-base-component',
  template: ``,
  standalone: false
})
export abstract class NhMutateBaseComponent<TFormData, TResult> extends NhMutateBaseTypeComponent<TFormData, TResult, NhAuthorization, NhAuthService> {
  constructor() {
    super(NhAuthService);
  }
}
