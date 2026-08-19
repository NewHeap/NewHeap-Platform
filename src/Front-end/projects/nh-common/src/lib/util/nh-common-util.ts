export const languageToCultureMap = {
  'nl': 'nl-NL',
  'en': 'en-US',
  'fr': 'fr-FR',
  'de': 'de-DE',
};

export const nameof = <T>(name: keyof T) => name;

export const IsDefined = <T>(value: T): value is NonNullable<T> => {
  return value !== undefined && value !== null;
}

export function enumKeysToArray(e: any) {
  return Object.keys(e).filter(k => typeof e[k as any] === 'number' || typeof e[k as any] === 'string');
}

export function enumValuesToArray(e: any) {
  return enumKeysToArray(e).map(k => e[k as any]);
}

export function enumIntValuesToArray(e: any) {
  return enumKeysToArray(e).map(k => e[k as any]).filter(k => typeof k === 'number');
}

export function enumStringValuesToArray(e: any) {
  return enumKeysToArray(e).map(k => e[k as any]).filter(k => typeof k === 'string');
}

export function getEmptyGuid(): string {
  return '00000000-0000-0000-0000-000000000000';
}

export function getRandomIdentifier(): string {
  let d = new Date().getTime();//Timestamp
  let d2 = ((typeof performance !== 'undefined') && performance.now && (performance.now()*1000)) || 0;//Time in microseconds since page-load or 0 if unsupported
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
    let r = Math.random() * 16;//random number between 0 and 16
    if(d > 0){//Use timestamp until depleted
      r = (d + r)%16 | 0;
      d = Math.floor(d/16);
    } else {//Use microseconds since page-load if supported
      r = (d2 + r)%16 | 0;
      d2 = Math.floor(d2/16);
    }
    return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
  });
}

export function groupBy<T>(items: any[], keyGetter: any) {
  return items.reduce(
    (result, item) => ({
      ...result,
      [keyGetter(item)]: [
        ...(result[keyGetter(item)] || []),
        item,
      ],
    }),
    {},
  );
}

export function uppercaseFirst(val?: string) {
  if(val) {
    return val && String(val[0]).toUpperCase() + String(val).slice(1);
  } else{
    return val;
  }
}
