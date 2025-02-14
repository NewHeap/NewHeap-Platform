export{}
declare global {
  interface Array<T>  {
    any(): boolean;
    firstOrDefault(): any;
    lastOrDefault(): any;
  }
}

Array.prototype.any = function() {
  return this && ((this?.length ?? 0) > 0);
}

Array.prototype.firstOrDefault = function(defaultValue: any = undefined) {
  return this && this.length > 0 ? this[0] : defaultValue;
}

Array.prototype.lastOrDefault = function(defaultValue: any = undefined) {
  return this && this.length > 0 ? this[this.length - 1] : defaultValue;
}
