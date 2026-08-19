export class NhStringUtil {
  public static upperFirst(inString: string|undefined|null): string|undefined|null {
    if(!inString || inString.length < 1) {
      return inString;
    }

    return `${inString[0].toUpperCase()}${inString.slice(1)}`;
  }

  public static lowerFirst(inString: string|undefined|null): string|undefined|null {
    if(!inString || inString.length < 1) {
      return inString;
    }

    return `${inString[0].toLowerCase()}${inString.slice(1)}`;
  }
}
