import {Component, Input} from "@angular/core";
import {MutationType, NhMutateBaseComponent, TaskResult} from "nh-common";
import { Address } from "../../models/address.models";
import {AddressService} from "../../services/address.service";

@Component({
  selector: 'app-address-mutate',
  styleUrls: ['component.scss'],
  templateUrl: 'component.html',
  standalone: false
})
export class MutateAddressComponent extends NhMutateBaseComponent<Address> {
  @Input() id: string|undefined;

  constructor(
    private readonly addressService: AddressService
  ) {
    super();
  }

  override async onNewFormData(mutationType: MutationType): Promise<Address> {
    if(mutationType === MutationType.Create) {
      return new Address({
      });
    } else {
      return await this.addressService.get<Address>(this.id!).lastValueFrom();
    }
  }

  override async onSubmitCreate(event: any): Promise<TaskResult<Address>> {
    const result = new TaskResult<Address>();
    let address = await this.addressService.create<Address>(this.formData).lastValueFrom();
    result.data = await this.addressService.get<Address>(address.id!).lastValueFrom();

    return result;
  }

  override async onSubmitUpdate(event: any): Promise<TaskResult<Address>> {
    const result = new TaskResult<Address>();
    await this.addressService.update<void>(this.id!, this.formData).lastValueFrom();
    result.data = await this.addressService.get<Address>(this.id!).lastValueFrom();

    return result;
  }
}
