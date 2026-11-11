import { relative } from 'node:path';
import { consumerSkillBundleName, consumerSkillNames, loadRules, repositoryRoot } from './lib.mjs';
import {
  loadSkillEvaluations,
  pluginEvals,
  renderPluginEvalSuite,
  stalePluginEvalSuiteFiles,
  validateSkillEvalSuiteDefinition
} from './skill-eval-suite.mjs';

const evaluations = await loadSkillEvaluations();
const rules = await loadRules();
const failures = [];
if (evaluations.schemaVersion !== 1 || !Array.isArray(evaluations.evals)) failures.push('Invalid skill eval schema.');
const knownRules = new Set(rules.map(rule => rule.id));
const ruleById = new Map(rules.map(rule => [rule.id, rule]));
const knownSkills = new Set([consumerSkillBundleName, ...consumerSkillNames, 'newheap-library-maintenance']);
const coveredRules = new Set();
const ids = new Set();

for (const item of evaluations.evals ?? []) {
  if (!item.id || ids.has(item.id)) failures.push(`Invalid or duplicate eval id: ${item.id}`);
  else if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(item.id)) failures.push(`${item.id}: eval ids must be kebab-case, because they name the generated case directories`);
  ids.add(item.id);
  if (!knownSkills.has(item.skill)) failures.push(`${item.id}: unknown skill ${item.skill}`);
  if (!item.prompt || !item.expectedOutcome) failures.push(`${item.id}: prompt and expectedOutcome are required`);
  if (!Array.isArray(item.expectedRules) || item.expectedRules.length === 0) failures.push(`${item.id}: expectedRules are required`);
  for (const rule of item.expectedRules ?? []) {
    if (!knownRules.has(rule)) failures.push(`${item.id}: unknown rule ${rule}`);
    else if (![consumerSkillBundleName, 'newheap-library-maintenance'].includes(item.skill) && !ruleById.get(rule).skills.includes(item.skill)) {
      failures.push(`${item.id}: ${rule} is not routed to ${item.skill}`);
    }
    coveredRules.add(rule);
  }
}

for (const rule of knownRules) if (!coveredRules.has(rule)) failures.push(`No skill eval covers ${rule}`);
for (const skill of knownSkills) if (!(evaluations.evals ?? []).some(item => item.skill === skill)) failures.push(`No eval targets ${skill}`);

failures.push(...await validateSkillEvalSuiteDefinition(evaluations));
let suiteFiles = 0;
if (failures.length === 0) {
  const suite = renderPluginEvalSuite(evaluations, rules);
  suiteFiles = suite.size;
  const stale = await stalePluginEvalSuiteFiles(suite);
  if (stale.length > 0) {
    failures.push(`The generated plugin eval suite is stale:\n${stale.map(path => `- ${relative(repositoryRoot, path).replaceAll('\\', '/')}`).join('\n')}\nRun npm run guidance:generate.`);
  }
}

if (failures.length > 0) throw new Error(failures.join('\n'));
console.log(`Validated ${evaluations.evals.length} skill evals covering ${knownRules.size} guidance rules and the generated plugin eval suite of ${pluginEvals(evaluations).length} cases in ${suiteFiles} files.`);
