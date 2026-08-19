import {Pipe, PipeTransform} from '@angular/core';
import {TranslateService} from '@ngx-translate/core';

@Pipe({
    name: 'nhBooleanToString',
    standalone: false
})
export class NhBooleanToStringPipe implements PipeTransform {

  constructor(private translateService: TranslateService) {

  }

  transform(value: boolean | undefined): string {
    if (value === undefined) {
      return '';
    }

    return value
      ? this.translateService.instant('Yes')
      : this.translateService.instant('No');
  }
}
