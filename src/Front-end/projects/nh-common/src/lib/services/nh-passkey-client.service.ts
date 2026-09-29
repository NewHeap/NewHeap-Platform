import {inject, Injectable, PLATFORM_ID} from '@angular/core';
import {isPlatformBrowser} from '@angular/common';
import {Base64} from 'js-base64';

/**
 * Runs WebAuthn ceremonies in the browser. The server returns the options in their JSON form
 * and expects the credential in its JSON form; this client converts both, with a fallback for
 * browsers without `PublicKeyCredential.parseCreationOptionsFromJSON` and `toJSON()`.
 */
@Injectable({providedIn: 'root'})
export class NhPasskeyClient {
  private readonly platformId: Object = inject(PLATFORM_ID);

  /**
   * Whether the browser supports passkeys. Always false during server-side rendering.
   */
  isSupported(): boolean {
    return isPlatformBrowser(this.platformId)
      && typeof window !== 'undefined'
      && typeof window.PublicKeyCredential !== 'undefined'
      && !!navigator.credentials;
  }

  /**
   * Creates a passkey for WebAuthn creation options and returns the credential JSON.
   * Rejects when the user cancels the prompt or the browser refuses the request.
   */
  async create(options: any): Promise<object> {
    this.ensureSupported();

    const parse = (PublicKeyCredential as any).parseCreationOptionsFromJSON;
    const publicKey: PublicKeyCredentialCreationOptions = typeof parse === 'function'
      ? parse.call(PublicKeyCredential, options)
      : this.creationOptionsFromJSON(options);

    const credential = await navigator.credentials.create({publicKey: publicKey});
    return this.credentialToJSON(this.requireCredential(credential));
  }

  /**
   * Signs WebAuthn request options with a passkey and returns the credential JSON.
   * Rejects when the user cancels the prompt or the browser refuses the request.
   */
  async get(options: any): Promise<object> {
    this.ensureSupported();

    const parse = (PublicKeyCredential as any).parseRequestOptionsFromJSON;
    const publicKey: PublicKeyCredentialRequestOptions = typeof parse === 'function'
      ? parse.call(PublicKeyCredential, options)
      : this.requestOptionsFromJSON(options);

    const credential = await navigator.credentials.get({publicKey: publicKey});
    return this.credentialToJSON(this.requireCredential(credential));
  }

  private ensureSupported(): void {
    if (!this.isSupported()) {
      throw new Error('Passkeys are not supported in this browser.');
    }
  }

  private requireCredential(credential: Credential | null): PublicKeyCredential {
    if (!credential || credential.type !== 'public-key') {
      throw new Error('No passkey was selected.');
    }

    return credential as PublicKeyCredential;
  }

  private creationOptionsFromJSON(json: any): PublicKeyCredentialCreationOptions {
    return {
      ...json,
      challenge: NhPasskeyClient.fromBase64Url(json.challenge),
      user: {...json.user, id: NhPasskeyClient.fromBase64Url(json.user.id)},
      excludeCredentials: (json.excludeCredentials ?? []).map((descriptor: any) => ({
        ...descriptor,
        id: NhPasskeyClient.fromBase64Url(descriptor.id)
      }))
    };
  }

  private requestOptionsFromJSON(json: any): PublicKeyCredentialRequestOptions {
    return {
      ...json,
      challenge: NhPasskeyClient.fromBase64Url(json.challenge),
      allowCredentials: (json.allowCredentials ?? []).map((descriptor: any) => ({
        ...descriptor,
        id: NhPasskeyClient.fromBase64Url(descriptor.id)
      }))
    };
  }

  private credentialToJSON(credential: PublicKeyCredential): object {
    const toJSON = (credential as any).toJSON;
    if (typeof toJSON === 'function') {
      return toJSON.call(credential);
    }

    const response: any = credential.response;
    const json: any = {
      id: credential.id,
      rawId: NhPasskeyClient.toBase64Url(credential.rawId),
      type: credential.type,
      authenticatorAttachment: (credential as any).authenticatorAttachment ?? undefined,
      clientExtensionResults: credential.getClientExtensionResults?.() ?? {},
      response: {
        clientDataJSON: NhPasskeyClient.toBase64Url(response.clientDataJSON)
      }
    };

    if (response.attestationObject) {
      json.response.attestationObject = NhPasskeyClient.toBase64Url(response.attestationObject);
      json.response.transports = typeof response.getTransports === 'function' ? response.getTransports() : [];
    }

    if (response.authenticatorData) {
      json.response.authenticatorData = NhPasskeyClient.toBase64Url(response.authenticatorData);
      json.response.signature = NhPasskeyClient.toBase64Url(response.signature);
      json.response.userHandle = response.userHandle ? NhPasskeyClient.toBase64Url(response.userHandle) : null;
    }

    return json;
  }

  private static fromBase64Url(value: string): ArrayBuffer {
    const bytes = Base64.toUint8Array(value);
    return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
  }

  private static toBase64Url(buffer: ArrayBuffer): string {
    return Base64.fromUint8Array(new Uint8Array(buffer), true);
  }
}
