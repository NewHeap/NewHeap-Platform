import { DatePipe } from '@angular/common';
import { Pipe, PipeTransform } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import {NhCommonModuleConfig} from "../models/config.models";

@Pipe({
    name: 'nhDate',
    pure: false,
    standalone: false
})
export class NhDatePipe implements PipeTransform {

  constructor(private moduleConfig: NhCommonModuleConfig) {
  }

  transform(value: any, pattern?: string): any {
    pattern ??= this.moduleConfig.defaultDateFormat;
    const datePipe: DatePipe = new DatePipe(this.moduleConfig.language);
    return datePipe.transform(value, pattern);
  }
}

@Pipe({
  name: 'nhDateTime',
  pure: false,
  standalone: false
})
export class NhDateTimePipe implements PipeTransform {

  constructor(private moduleConfig: NhCommonModuleConfig) {
  }

  transform(value: any, pattern?: string): any {
    pattern ??= this.moduleConfig.defaultDateTimeFormat;
    const datePipe: NhDatePipe = new NhDatePipe(this.moduleConfig);
    return datePipe.transform(value, pattern);
  }
}

@Pipe({
  name: 'nhDateUtc',
  pure: false,
  standalone: false
})
export class NhDateUtcPipe implements PipeTransform {

  constructor(private moduleConfig: NhCommonModuleConfig) {
  }

  transform(value: any, pattern: string = 'dd-MM-yyyy HH:mm:ss'): any {
    if(value && !value.toString().endsWith('Z')) {
      value = value + 'Z';
    }
    const datePipe: DatePipe = new DatePipe(this.moduleConfig.language);
    return datePipe.transform(value, pattern);
  }
}
