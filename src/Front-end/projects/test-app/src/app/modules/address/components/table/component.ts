import {Component, input, OnInit, ViewChild} from "@angular/core";

import { DatatableComponent } from "@swimlane/ngx-datatable";
import {Observable} from "rxjs";
import {MutateAddressComponent} from "../mutate/component";
import {
  CollectionHttpResponse,
  MutationType,
  NhCollectionBaseComponent,
  NhModalConfirmComponent,
  NhModalOptions
} from "nh-common";
import {Address, AddressCollectionHttpRequestOptions} from "../../models/address.models";
import { AddressService } from "../../services/address.service";

@Component({
    selector: 'app-address-table',
    templateUrl: './component.html',
    styleUrls: ['./component.scss'],
    standalone: false
})
export class TableAddressComponent extends NhCollectionBaseComponent<Address> implements OnInit {
  @ViewChild('myTable') table?: DatatableComponent;
  readonly partialLocalStorageKey = input<string>('default-address-collection');
  override requestOptions = new AddressCollectionHttpRequestOptions();

  constructor(
    private addressService: AddressService
  ) {
    super();
  }

  getInitialRequestOptions() {
    return new AddressCollectionHttpRequestOptions();
  }

  override getLocalStoragePartialKey(): string | null {
    return this.partialLocalStorageKey();
  }

  override async beforeLoad() {

  }

  async onLoad(requestOptions: AddressCollectionHttpRequestOptions) {
    return <Observable<CollectionHttpResponse<Address>>>this.addressService.getCollection(requestOptions);
  }

  override async afterLoad() {

  }

  async showMutateModal(address: Address | undefined) {
    const mutationType = address ? MutationType.Update : MutationType.Create;

    const modal = this.modalService.open(MutateAddressComponent, new NhModalOptions({}));
    modal!.contentComponent!.id = address?.id;

    const create$ = modal.contentComponent!.created.subscribe(address => {
      create$?.unsubscribe();
      this.load().then();
      modal.close();
    });
    const update$ = modal.contentComponent!.updated.subscribe(address => {
      update$?.unsubscribe();
      this.load().then();
      modal.close();
    });

    modal.contentComponent?.newFormData(mutationType);
  }

  async onTableActivate(event: any) {
    if(event?.type === 'dblclick') {
      const row = event.row as Address;
      await this.nhRouterService.navigate({ id: '/address/view', arguments: { id: row.id } });
    }
  }

  showDeleteConfirmModal(id: string) {
    const modal = this.modalService.open(NhModalConfirmComponent, new NhModalOptions({
      title: this.translateService.instant('modal.confirm.address')
    }));
    modal.contentComponent!.message = this.translateService.instant('modal.confirm.message');
    modal.contentComponent!.onConfirm = async () => {
      await this.addressService.delete(id).lastValueFrom();
      this.load().then();
      modal.close();
    };
    modal.contentComponent!.onCancel = () => {
      modal.close();
    };
  }
}
