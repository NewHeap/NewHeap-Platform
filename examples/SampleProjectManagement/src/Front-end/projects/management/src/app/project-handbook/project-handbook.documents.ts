/** One Markdown document of the handbook bundle, by its path in the bundle. */
export interface ProjectHandbookDocument {
  readonly path: string;
  readonly markdown: string;
}

/**
 * SPM-336: the team's handbook as the bundle a repository or an upload would give: Markdown files
 * by path. The portal treats their text as untrusted, as it would treat a file a person uploaded.
 * The diagrams use the portal's `flow` language (`A -> B` per line); the last one in the release
 * guide has a mistake on purpose, so the page shows how a diagram that cannot be drawn looks.
 */
export const PROJECT_HANDBOOK: readonly ProjectHandbookDocument[] = [
  {
    path: 'README.md',
    markdown: [
      '# Project handbook',
      '',
      'How the sample team takes a project from intake to release. New members start with',
      '[onboarding](guides/onboarding.md); the [release guide](guides/release.md#checklist) says what a project needs',
      'before it is done.',
      '',
      '## The project flow',
      '',
      '```flow',
      'Intake -> Draft -> Active -> Done',
      'Draft -> On hold',
      '```',
      '',
      '## Untrusted text',
      '',
      'These files come from the team, so the portal shows them as text: <b>HTML</b> stays text, and',
      '[a script link](javascript:alert(1)) or [a file](../../secrets.txt) outside the handbook is no link.'
    ].join('\n')
  },
  {
    path: 'guides/onboarding.md',
    markdown: [
      '# Onboarding',
      '',
      '## First day',
      '',
      '- Read the [project handbook](../README.md) and its [project flow](../README.md#the-project-flow).',
      '- Read the [release guide](release.md) before your first release.',
      '',
      '## Access',
      '',
      'People with the project view permission see the projects of their division; managers also edit them.',
      'The libraries behind the portal live in the',
      '[NewHeap Platform repository](https://github.com/NewHeap/NewHeap-Platform).'
    ].join('\n')
  },
  {
    path: 'guides/release.md',
    markdown: [
      '# Releasing a project',
      '',
      '## Checklist',
      '',
      '1. Every task of the project is done.',
      '2. The project board shows no open proposals.',
      '3. The [onboarding](onboarding.md#access) access rules still hold.',
      '',
      '## Release flow',
      '',
      '```flow',
      'Ready -> Review -> Release',
      'Review -> Rework',
      '```',
      '',
      '## A diagram with a mistake',
      '',
      'The second line names a step without an arrow, so the handbook shows the source and the reason.',
      '',
      '```flow',
      'Review -> Release',
      'Rollback',
      '```'
    ].join('\n')
  }
];
