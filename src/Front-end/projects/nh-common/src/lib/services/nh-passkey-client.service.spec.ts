import {PLATFORM_ID} from '@angular/core';
import {TestBed} from '@angular/core/testing';

import {NhPasskeyClient} from './nh-passkey-client.service';

describe('NhPasskeyClient', () => {
  function create(platform: string): NhPasskeyClient {
    TestBed.configureTestingModule({
      providers: [{provide: PLATFORM_ID, useValue: platform}]
    });

    return TestBed.inject(NhPasskeyClient);
  }

  function bytes(...values: number[]): ArrayBuffer {
    return new Uint8Array(values).buffer;
  }

  it('is unsupported during server-side rendering', async () => {
    const client = create('server');

    expect(client.isSupported()).toBeFalse();
    await expectAsync(client.get({challenge: 'AQID'})).toBeRejected();
  });

  it('converts request options and the assertion without the browser JSON helpers', async () => {
    const client = create('browser');
    if (!client.isSupported()) {
      pending('This browser does not expose WebAuthn.');
      return;
    }

    const parseDescriptor = Object.getOwnPropertyDescriptor(PublicKeyCredential, 'parseRequestOptionsFromJSON');
    Object.defineProperty(PublicKeyCredential, 'parseRequestOptionsFromJSON', {value: undefined, configurable: true});

    const get = spyOn(navigator.credentials, 'get').and.resolveTo({
      id: 'CQo',
      type: 'public-key',
      rawId: bytes(9, 10),
      authenticatorAttachment: 'platform',
      getClientExtensionResults: () => ({}),
      response: {
        clientDataJSON: bytes(1),
        authenticatorData: bytes(2),
        signature: bytes(3),
        userHandle: bytes(4, 5)
      }
    } as unknown as Credential);

    try {
      const credential: any = await client.get({
        challenge: 'AQID',
        rpId: 'localhost',
        allowCredentials: [{type: 'public-key', id: 'CQo'}]
      });

      const publicKey = get.calls.mostRecent().args[0]!.publicKey!;
      expect(new Uint8Array(publicKey.challenge as ArrayBuffer)).toEqual(new Uint8Array([1, 2, 3]));
      expect(new Uint8Array(publicKey.allowCredentials![0].id as ArrayBuffer)).toEqual(new Uint8Array([9, 10]));
      expect(publicKey.rpId).toBe('localhost');

      expect(credential).toEqual(jasmine.objectContaining({
        id: 'CQo',
        rawId: 'CQo',
        type: 'public-key',
        authenticatorAttachment: 'platform'
      }));
      expect(credential.response).toEqual({
        clientDataJSON: 'AQ',
        authenticatorData: 'Ag',
        signature: 'Aw',
        userHandle: 'BAU'
      });
    } finally {
      if (parseDescriptor) {
        Object.defineProperty(PublicKeyCredential, 'parseRequestOptionsFromJSON', parseDescriptor);
      } else {
        delete (PublicKeyCredential as any).parseRequestOptionsFromJSON;
      }
    }
  });

  it('rejects when the user cancels the prompt', async () => {
    const client = create('browser');
    if (!client.isSupported()) {
      pending('This browser does not expose WebAuthn.');
      return;
    }

    spyOn(navigator.credentials, 'create').and.resolveTo(null);

    await expectAsync(client.create({
      challenge: 'AQID',
      rp: {id: 'localhost', name: 'Tests'},
      user: {id: 'AQ', name: 'user', displayName: 'User'},
      pubKeyCredParams: [{type: 'public-key', alg: -7}]
    })).toBeRejected();
  });
});
