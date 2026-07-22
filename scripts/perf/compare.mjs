import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { parseArgs, requireArg } from './lib/args.mjs';
import { compareArtifacts, exitCodeForComparison } from './lib/compareLogic.mjs';
import { BUDGETS_PATH } from './lib/schema.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

function loadJson(filePath) {
  const resolvedPath = path.resolve(filePath);
  if (!fs.existsSync(resolvedPath)) {
    throw new Error(`File not found: ${resolvedPath}`);
  }
  return JSON.parse(fs.readFileSync(resolvedPath, 'utf8'));
}

function loadBudgets(budgetsPath) {
  const resolvedPath = path.resolve(budgetsPath ?? path.join(ROOT, BUDGETS_PATH));
  return loadJson(resolvedPath);
}

function ensureParentDir(filePath) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
}

export function runComparison(options) {
  const baseArtifact = loadJson(options.base);
  const candidateArtifact = loadJson(options.candidate);
  const budgets = loadBudgets(options.budgets);
  return compareArtifacts(baseArtifact, candidateArtifact, budgets);
}

function main() {
  const parsed = parseArgs(process.argv);
  const result = runComparison({
    base: requireArg(parsed, 'base'),
    candidate: requireArg(parsed, 'candidate'),
    budgets: typeof parsed.budgets === 'string' ? parsed.budgets : null,
  });

  const output = `${JSON.stringify(result, null, 2)}\n`;
  console.log(output.trimEnd());

  if (typeof parsed.output === 'string') {
    const outputPath = path.resolve(parsed.output);
    ensureParentDir(outputPath);
    fs.writeFileSync(outputPath, output, 'utf8');
  }

  process.exit(exitCodeForComparison(result));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main();
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exit(1);
  }
}
