import assert from 'node:assert/strict';
import { relative } from 'node:path';
import { consumerSkillBundleName, loadRules } from './lib.mjs';
import {
  loadSkillEvaluations,
  pluginEvalSuiteRoot,
  renderPluginEvalSuite,
  validateSkillEvalSuiteDefinition
} from './skill-eval-suite.mjs';

const rules = await loadRules();
const evaluations = await loadSkillEvaluations();
assert.deepEqual(await validateSkillEvalSuiteDefinition(evaluations), []);

const backendRule = rules.find(rule => rule.skills.length === 1 && rule.skills[0] === 'newheap-backend-development');
const frontendRule = rules.find(rule => rule.skills.length === 1 && rule.skills[0] === 'newheap-frontend-development');
const fixture = {
  schemaVersion: 1,
  defaultWorkspace: 'portal',
  workspaces: {
    empty: { description: 'An empty repository.' },
    portal: { description: 'A bootstrapped portal.', bootstrap: ['--name', 'Example.Portal'] }
  },
  evals: [
    {
      id: 'router-case',
      skill: consumerSkillBundleName,
      prompt: 'Add an API and a page.',
      expectedRules: [backendRule.id, frontendRule.id],
      expectedOutcome: 'Uses "quoted" names: keeps\nnewlines, colons and # hashes.',
      checks: [
        { name: 'mentions-option', type: 'response_matches', pattern: '--profile\\s+public-website', flags: 'i' },
        { name: 'props-file', type: 'file_exists', path: 'src/Back-end/Directory.Packages.props' },
        { name: 'no-media', type: 'module_not_read', skill: 'newheap-media-development' }
      ]
    },
    {
      id: 'empty-case',
      skill: 'newheap-consumer-development',
      prompt: 'Prepare this empty repository.',
      expectedRules: ['nh-consumer-bootstrap-sequence'],
      expectedOutcome: 'Bootstraps the foundation.',
      workspace: 'empty'
    },
    {
      id: 'maintainer-case',
      skill: 'newheap-library-maintenance',
      prompt: 'Change a public option.',
      expectedRules: [backendRule.id],
      expectedOutcome: 'Tests and samples.'
    }
  ]
};
assert.deepEqual(await validateSkillEvalSuiteDefinition(fixture), []);

const files = new Map([...renderPluginEvalSuite(fixture, rules)].map(([path, content]) => [relative(pluginEvalSuiteRoot, path).replaceAll('\\', '/'), content]));
assert.ok(![...files.keys()].some(path => path.startsWith('maintainer-case/')), 'skills outside the plugin are not part of the plugin suite');
assert.deepEqual([...files.keys()].filter(path => path.startsWith('router-case/')).sort(), [
  'router-case/case.yaml',
  'router-case/graders/check-mentions-option.md',
  'router-case/graders/check-no-media.md',
  'router-case/graders/check-props-file.md',
  'router-case/graders/module-backend.md',
  'router-case/graders/module-frontend.md',
  'router-case/graders/outcome.md',
  'router-case/graders/skill-fired.md',
  'router-case/prompt.md',
  'router-case/scaffold.sh'
]);

function frontmatter(source) {
  const match = /^---\n([\s\S]*?)\n---\n/.exec(source);
  assert.ok(match, 'generated Markdown starts with frontmatter');
  const values = {};
  for (const line of match[1].split('\n')) {
    if (line.startsWith('#')) continue;
    const separator = line.indexOf(': ');
    const key = line.slice(0, separator);
    const value = line.slice(separator + 2);
    values[key] = value.startsWith('"') || value.startsWith('[') ? JSON.parse(value) : value;
  }
  return { values, body: source.slice(match[0].length).trim() };
}

const prompt = frontmatter(files.get('router-case/prompt.md'));
assert.equal(prompt.body, 'Add an API and a page.');
assert.equal(prompt.values.expected_outcome, fixture.evals[0].expectedOutcome);
assert.deepEqual(prompt.values.tags, ['router', 'backend', 'frontend']);
assert.deepEqual(prompt.values.allowed_tools, ['Read', 'Glob', 'Grep', 'Skill', 'Write', 'Edit']);

const outcome = frontmatter(files.get('router-case/graders/outcome.md'));
assert.equal(outcome.values.type, 'llm');
assert.equal(outcome.values.weight, '2', 'the rubric weighs as much as the scored checks together');
assert.ok(outcome.body.includes(fixture.evals[0].expectedOutcome));

const check = frontmatter(files.get('router-case/graders/check-mentions-option.md')).values;
assert.equal(check.type, 'regex');
assert.equal(check.pattern, '--profile\\s+public-website');
assert.equal(check.flags, 'i');
const notRead = frontmatter(files.get('router-case/graders/check-no-media.md')).values;
assert.equal(notRead.max, '0');
assert.equal(notRead.arm, 'with-only');

const moduleRead = new RegExp(frontmatter(files.get('router-case/graders/module-backend.md')).values.input_match);
assert.ok(moduleRead.test(JSON.stringify({ file_path: 'C:\\stage\\skills\\newheap-platform-development\\skills\\backend\\SKILL.md' })));
assert.ok(moduleRead.test(JSON.stringify({ file_path: '/stage/skills/newheap-platform-development/skills/backend/SKILL.md' })));
assert.ok(!moduleRead.test(JSON.stringify({ file_path: '/stage/skills/newheap-platform-development/skills/backend/references/x.md' })));
assert.ok(!moduleRead.test(JSON.stringify({ file_path: '/stage/skills/newheap-platform-development/skills/frontend/SKILL.md' })));

const skillFired = new RegExp(frontmatter(files.get('router-case/graders/skill-fired.md')).values.input_match);
assert.ok(skillFired.test(JSON.stringify({ skill: 'newheap-platform:newheap-platform-development' })));
assert.ok(skillFired.test(JSON.stringify({ skill: 'newheap-platform-development' })));
assert.ok(!skillFired.test(JSON.stringify({ skill: 'other-skill' })));

assert.ok(files.get('router-case/scaffold.sh').includes('fixtures/portal'));
assert.ok(!files.get('empty-case/scaffold.sh').includes('fixtures/'), 'an empty workspace copies no fixture');
assert.equal(frontmatter(files.get('empty-case/graders/outcome.md')).values.weight, '1');

const invalid = structuredClone(fixture);
invalid.workspaces.loop = { description: 'Loops.', extends: 'loop' };
invalid.workspaces.outside = { description: 'Outside.', files: 'skills' };
invalid.evals[0].workspace = 'missing';
invalid.evals[0].checks.push(
  { name: 'mentions-option', type: 'response_matches', pattern: 'x' },
  { name: 'bad-pattern', type: 'response_matches', pattern: '(' },
  { name: 'escaping-path', type: 'file_exists', path: '../outside.txt' },
  { name: 'unknown-module', type: 'module_not_read', skill: 'newheap-library-maintenance' },
  { name: 'extra-field', type: 'file_exists', path: 'a.txt', pattern: 'a' }
);
invalid.evals[2].workspace = 'empty';
const failures = (await validateSkillEvalSuiteDefinition(invalid)).join('\n');
for (const expected of [
  'Skill eval workspace loop extends itself.',
  'Workspace outside: files must name a directory below skill-evals/fixtures.',
  'router-case: unknown workspace missing',
  'router-case: check mentions-option needs a unique kebab-case name.',
  'router-case: check bad-pattern has an invalid pattern',
  'router-case: check escaping-path needs a relative workspace path',
  'router-case: check unknown-module names unknown consumer module',
  'router-case: check extra-field has unsupported field pattern.',
  'maintainer-case: newheap-library-maintenance is not distributed in the newheap-platform plugin'
]) {
  assert.ok(failures.includes(expected), `expected validation failure: ${expected}\n${failures}`);
}

console.log('Verified plugin eval suite validation, case rendering, graders and plugin-fired indicators.');
