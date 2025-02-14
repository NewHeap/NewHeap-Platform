import {Component, computed, effect, input, Input} from '@angular/core'
import {TaskResult} from "nh-common";
import {JsonPipe} from "@angular/common";

@Component({
  selector: 'nh-shared-error',
  templateUrl: 'component.html',
  styleUrls: ['component.scss'],
  imports: [

  ],
  standalone: true
})
export class NhSharedErrorComponent {
  key = input.required<string | undefined>();
  errors = input.required<TaskResult<any> | undefined>();


  get displayErrors() {
    if(this.key() == undefined) {
      return this.errors()?.items.reduce((a,b) => [...a, ...b.errorMessages] ,<string[]>[]) || [];
    }

    return this.errors()?.items.find(x => x.name?.toLowerCase() === this.key()?.toLowerCase())?.errorMessages || []
  }
}
