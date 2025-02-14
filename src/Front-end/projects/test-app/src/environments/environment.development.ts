import {developmentBaseEnvironment} from "./environment.base.development";

const env: Partial<typeof developmentBaseEnvironment> = {};

export const environment = Object.assign(developmentBaseEnvironment, env);
