
export class NhXmlUtil {
  public static getCustomAttribute(entity: {custom_attributes?: string | null | undefined}, selector: string) {
    try {
      const xmlStr = entity.custom_attributes || '';
      const parser = new DOMParser();
      const doc = parser.parseFromString(xmlStr, "application/xml");

      const errorNode = doc.querySelector("parsererror");
      if (errorNode) {
        return null;
      } else {
        return doc.querySelector(selector);
      }
    } catch{
      return null;
    }

  }

  public static getCustomAttributeValue(entity: {custom_attributes?: string | null | undefined}, selector: string) {
    return this.getCustomAttribute(entity, selector)?.innerHTML;
  }

  public static setCustomAttributeValue(entity: {custom_attributes?: string | null | undefined}, selector: string, value: string) {
    const xmlStr = entity.custom_attributes || '';
    const parser = new DOMParser();
    const doc = parser.parseFromString(xmlStr, "application/xml");
    const errorNode = doc.querySelector("parsererror");
    if (errorNode) {
      return false;
    } else {
      const attr = doc.querySelector(selector);
      if(!attr) {
        return false;
      }
      attr.innerHTML = value;

      entity.custom_attributes = doc.documentElement.outerHTML;
      return true;
    }
  }
}
