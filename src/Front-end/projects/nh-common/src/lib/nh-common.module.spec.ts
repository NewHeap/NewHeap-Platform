import './prototype-extensions/observable.extensions';
import {NhCommonModuleConfig} from './models/config.models';
import {NhCommonModule} from './nh-common.module';

describe('NhCommonModule provider scope', () => {
  it('preserves legacy module providers while forRoot adds only config and auth mappings', () => {
    const moduleInjectorProviders = (NhCommonModule as never as {
      ɵinj: {providers: unknown[]}
    }).ɵinj.providers;
    const rootRegistration = NhCommonModule.forRoot(
      new NhCommonModuleConfig(),
      class {} as never
    );

    expect(moduleInjectorProviders.length).toBeGreaterThan(0);
    expect(rootRegistration.providers?.length).toBe(2);
  });
});
