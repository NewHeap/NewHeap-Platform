import {baseEnvironment} from "./environment.base";

const env: Partial<typeof baseEnvironment> = {
  production: false,
  name: 'staging',
  baseUrl: 'https://staging.test-app.local',
  cookieDomain: 'test-app.local'
};
export const stagingBaseEnvironment = Object.assign(baseEnvironment, env);

