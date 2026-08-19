export const SAMPLE_AUTHORIZATION_IDS = {
  northDivision: 'b14a1178-8bd7-4e87-845f-e0d89b63f099',
  southDivision: '74e0420a-186b-4b4b-af08-91b4379fba2c',
  alphaProject: '87534f33-bd4b-43ed-8ce5-8861d320271d',
  betaProject: 'b926cbb0-7dc6-4504-8bcc-384ee787d642'
} as const;

export interface AuthorizationDemoAccount {
  labelKey: string;
  email: string;
  expectedAccessKey: string;
}

export const AUTHORIZATION_DEMO_ACCOUNTS: readonly AuthorizationDemoAccount[] = [
  {
    labelKey: 'project.authorization-role-manager',
    email: 'sample@example.test',
    expectedAccessKey: 'project.authorization-role-manager-access'
  },
  {
    labelKey: 'project.authorization-role-viewer',
    email: 'viewer@example.test',
    expectedAccessKey: 'project.authorization-role-viewer-access'
  },
  {
    labelKey: 'project.authorization-role-division-editor',
    email: 'division-editor@example.test',
    expectedAccessKey: 'project.authorization-role-division-editor-access'
  },
  {
    labelKey: 'project.authorization-role-project-editor',
    email: 'project-editor@example.test',
    expectedAccessKey: 'project.authorization-role-project-editor-access'
  }
] as const;

export interface AuthorizationProbeSample {
  level: string;
  requiredPermission: string;
  message: string;
  activeDivisionId?: string;
  projectId?: string;
  roles: string[];
}

export interface RuntimeAuthorizationClaimSample {
  type: string;
  value: string;
}

export interface AuthenticationOverrideProbeSample {
  authenticationService: string;
  tokenClaimStrategy: string;
  requestClaimStrategy: string;
  requestTransformationApplied: boolean;
  userId?: string;
  activeDivisionId?: string;
  runtimeClaims: RuntimeAuthorizationClaimSample[];
  traceIdentifier: string;
}
