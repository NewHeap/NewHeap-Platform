import {stagingBaseEnvironment} from "./environment.base.staging";

const env: Partial<typeof stagingBaseEnvironment> = {};

export const environment = Object.assign(stagingBaseEnvironment, env);
