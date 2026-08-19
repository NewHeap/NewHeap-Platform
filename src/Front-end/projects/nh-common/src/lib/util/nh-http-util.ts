export class NhHttpUtil {
  public static filenameFromContentDisposition(contentDisposition: string): string {
    let filename = "";
    if (contentDisposition && contentDisposition.indexOf('attachment') !== -1) {
      const filenameRegex = /filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/;
      const matches = filenameRegex.exec(contentDisposition);
      if (matches != null && matches[1]) {
        filename = matches[1].replace(/['"]/g, '');
      }
    }

    return filename;
  }

  public static objectToFormData(obj: any, formData: FormData = new FormData(), namespace?: string): FormData {
    if (obj === null || obj === undefined) {
      return formData;
    }

    if (Array.isArray(obj)) {
      obj.forEach((item, index) => {
        this.objectToFormData(item, formData, namespace ? `${namespace}[${index}]` : `${index}`);
      });
    } else if (typeof obj === 'object') {
      Object.keys(obj).forEach(key => {
        if (obj.hasOwnProperty(key) && obj[key] !== undefined) {
          this.objectToFormData(obj[key], formData, namespace ? `${namespace}.${key}` : key);
        }
      });
    } else {
      formData.append(namespace || '', obj);
    }

    return formData;
  }
}
