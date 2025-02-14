// This file will be replaced during build by using the `fileReplacements` array.
import {developmentBaseEnvironment} from "./environment.base.development";

const env: Partial<typeof developmentBaseEnvironment> = {};

export const environment = Object.assign(developmentBaseEnvironment, env);

