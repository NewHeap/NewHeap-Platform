import {productionBaseEnvironment} from "./environment.base.production";

const env: Partial<typeof productionBaseEnvironment> = {};

export const environment = Object.assign(productionBaseEnvironment, env);
