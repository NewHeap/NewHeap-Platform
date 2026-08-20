import { resolve } from 'node:path';
import { consumerSkillBundleName, consumerSkillNames, loadRules, readJson, repositoryRoot } from './lib.mjs';

const evaluations = await readJson(resolve(repositoryRoot, 'skill-evals', 'evals.json'));
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

if (failures.length > 0) throw new Error(failures.join('\n'));
console.log(`Validated ${evaluations.evals.length} skill evals covering ${knownRules.size} guidance rules.`);
