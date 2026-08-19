import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const defaultRepositoryRoot = resolve(scriptDirectory, '..', '..');
const argumentsList = process.argv.slice(2);
const rootIndex = argumentsList.indexOf('--root');
const repositoryRoot = rootIndex >= 0
  ? resolve(argumentsList[rootIndex + 1] ?? '')
  : defaultRepositoryRoot;

if (rootIndex >= 0 && !argumentsList[rootIndex + 1]) {
  throw new Error('--root requires a repository path.');
}

const git = spawnSync('git', ['ls-files', '--cached', '--others', '--exclude-standard', '-z'], {
  cwd: repositoryRoot,
  encoding: 'utf8'
});

if (git.status !== 0) {
  throw new Error(git.stderr || `Unable to list tracked files in ${repositoryRoot}.`);
}

const excludedPaths = new Set([
  'tools/security/test-validate-public-source.mjs',
  'tools/security/validate-public-source.mjs'
]);
const safeLiteralValues = new Set([
  '',
  'change-me',
  'guest',
  'newheap123!',
  'placeholder',
  'postgres',
  'sample-secret',
  'sample-token',
  'sample123!',
  'secret',
  'test'
]);
const safeDatabaseHosts = [
  /^\.$/,
  /^\(localdb\)/i,
  /^127\.0\.0\.1$/,
  /^localhost$/i,
  /^postgres(?:ql)?$/i,
  /^sqlserver$/i,
  /^<[^>]+>$/,
  /^\$\{[^}]+}$/,
  /example/i,
  /placeholder/i,
  /your[-_]/i
];
const highConfidencePatterns = [
  /\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,})\b/g,
  /\b(?:AKIA|ASIA)[A-Z0-9]{16}\b/g,
  /\bsk_live_[A-Za-z0-9]{16,}\b/g,
  /\bsk-(?:proj-)?[A-Za-z0-9_-]{20,}\b/g,
  /\bxox[baprs]-[A-Za-z0-9-]{20,}\b/g
];

function lineNumberAt(source, index) {
  return source.slice(0, index).split('\n').length;
}

function isSafeLiteral(value) {
  const normalized = value.trim().toLowerCase();
  return safeLiteralValues.has(normalized)
    || /^(?:dummy|example|fake|sample|test)[-_a-z0-9!]*$/i.test(value.trim())
    || /^<[^>]+>$/.test(value.trim())
    || /^\$\{[^}]+}$/.test(value.trim())
    || /^%[^%]+%$/.test(value.trim());
}

function addMatches(findings, path, source, rule, pattern) {
  pattern.lastIndex = 0;
  for (const match of source.matchAll(pattern)) {
    findings.push({ path, line: lineNumberAt(source, match.index ?? 0), rule });
  }
}

function scanSource(path, source) {
  const findings = [];
  addMatches(
    findings,
    path,
    source,
    'private-key',
    /-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----/g
  );
  addMatches(
    findings,
    path,
    source,
    'credentialed-url',
    /\b[a-z][a-z0-9+.-]*:\/\/[^\s/:]+:[^@\s]+@/gi
  );
  for (const pattern of highConfidencePatterns) {
    addMatches(findings, path, source, 'provider-token', pattern);
  }

  const assignmentPattern = /\b(?:password|passwd|clientSecret|secretKey|apiKey)\b\s*[:=]\s*["']([^"'\r\n]*)["']/gi;
  for (const match of source.matchAll(assignmentPattern)) {
    if (!isSafeLiteral(match[1])) {
      findings.push({
        path,
        line: lineNumberAt(source, match.index ?? 0),
        rule: 'literal-secret-assignment'
      });
    }
  }

  const stringLiteralPattern = /(["'`])([^"'`\r\n]*)\1/g;
  for (const literalMatch of source.matchAll(stringLiteralPattern)) {
    const literal = literalMatch[2];
    if (!/\b(?:Database|Initial Catalog)\s*=/i.test(literal)) continue;

    const passwordMatch = literal.match(/\b(?:Password|Pwd)\s*=\s*([^;\s]+)/i);
    if (passwordMatch && !isSafeLiteral(passwordMatch[1])) {
      findings.push({
        path,
        line: lineNumberAt(source, literalMatch.index ?? 0),
        rule: 'connection-string-password'
      });
    }

    const hostMatch = literal.match(/\b(?:Server|Data Source|Host)\s*=\s*([^;]+)/i);
    if (hostMatch) {
      const host = hostMatch[1].trim();
      if (!safeDatabaseHosts.some(pattern => pattern.test(host))) {
        findings.push({
          path,
          line: lineNumberAt(source, literalMatch.index ?? 0),
          rule: 'external-database-endpoint'
        });
      }
    }
  }

  return findings;
}

const candidateFiles = git.stdout.split('\0').filter(Boolean);
const findings = [];

for (const path of candidateFiles) {
  const normalizedPath = path.replaceAll('\\', '/');
  if (excludedPaths.has(normalizedPath)) continue;

  let content;
  try {
    content = await readFile(resolve(repositoryRoot, path));
  } catch (error) {
    if (error?.code === 'ENOENT') continue;
    throw error;
  }
  if (content.includes(0)) continue;
  findings.push(...scanSource(normalizedPath, content.toString('utf8')));
}

findings.sort((left, right) => left.path.localeCompare(right.path)
  || left.line - right.line
  || left.rule.localeCompare(right.rule));

if (findings.length > 0) {
  console.error('Public-source validation failed. Matched values are intentionally redacted:');
  for (const finding of findings) {
    console.error(`- ${finding.path}:${finding.line} [${finding.rule}]`);
  }
  process.exit(1);
}

console.log(`Public-source validation passed for ${candidateFiles.length} public candidate files.`);
