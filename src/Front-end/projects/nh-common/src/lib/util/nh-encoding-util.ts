export class NhEncodingUtil {

  public static convertBlobToBase64(blob: Blob): Promise<string|ArrayBuffer|null> {
    return new Promise<string|ArrayBuffer|null>((resolve, reject) => {
        const reader = new FileReader();
        reader.onerror = reject;
        reader.onload = () => {
          resolve(reader.result);
        };

        reader.readAsDataURL(blob);
    });
  }
}
