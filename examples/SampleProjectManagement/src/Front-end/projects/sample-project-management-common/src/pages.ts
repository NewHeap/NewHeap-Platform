// The pages of the shared library that routes load lazily, apart from the barrel: a dynamic import of
// the barrel keeps all its exports in the initial bundle that already imports it.
export * from './lib/sample-login.component';
export * from './lib/sample-profile.component';
export * from './lib/background-operations-page.component';
