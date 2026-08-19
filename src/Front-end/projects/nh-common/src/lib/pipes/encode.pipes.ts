import {Pipe, PipeTransform} from '@angular/core';

@Pipe({
    name: 'nhUrlEncode',
    standalone: false
})
export class NhUrlEncodePipe implements PipeTransform {
  transform(value: string | undefined | null): string|undefined|null {
    if (!value) {
      return value;
    }

    return encodeURIComponent(value ?? '');
  }
}
