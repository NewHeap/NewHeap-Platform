import {baseEnvironment} from "./environment.base";

const env: Partial<typeof baseEnvironment> = {
  production: false,
  name: 'development'
};

export const developmentBaseEnvironment = Object.assign(baseEnvironment, env);
