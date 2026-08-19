import {
  NhCommonModuleConfig,
  NhFormDropDownNhCommonModuleConfig,
  NhHttpNhCommonModuleConfig
} from './config.models';

describe('NhCommonModuleConfig defaults', () => {
  it('keeps opt-in HTTP and dropdown behavior backward compatible', () => {
    const config = new NhCommonModuleConfig();

    expect(config.http.deduplicateGetRequests).toBeFalse();
    expect(config.formDropdown.deferLazyLoadUntilOpened).toBeFalse();
  });

  it('supports explicitly enabling the preferred behavior', () => {
    const config = new NhCommonModuleConfig({
      http: new NhHttpNhCommonModuleConfig({deduplicateGetRequests: true}),
      formDropdown: new NhFormDropDownNhCommonModuleConfig({deferLazyLoadUntilOpened: true})
    });

    expect(config.http.deduplicateGetRequests).toBeTrue();
    expect(config.formDropdown.deferLazyLoadUntilOpened).toBeTrue();
  });

  it('preserves nested configuration object references', () => {
    const http = new NhHttpNhCommonModuleConfig({deduplicateGetRequests: true});
    const formDropdown = new NhFormDropDownNhCommonModuleConfig({deferLazyLoadUntilOpened: true});
    const config = new NhCommonModuleConfig({http, formDropdown});

    expect(config.http).toBe(http);
    expect(config.formDropdown).toBe(formDropdown);
  });
});
