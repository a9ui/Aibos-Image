import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { isDeepStrictEqual } from 'node:util';

import { expect, it } from 'vitest';

import { type AlbumMutation, mutateAlbums, readAlbums } from './albums';
import { canonicalizeParityContractBytes, parityContractSha256 } from '../../scripts/parity-contract-hash.mjs';
import {
  type SearchHistoryMutation,
  mutateSearchHistory,
  normalizeSearchHistoryQuery,
  readSearchHistory,
  searchHistoryComparisonKey,
} from './searchHistory';

type JsonObject = Record<string, unknown>;

interface InitialState {
  mode: 'missing' | 'json' | 'raw';
  document?: JsonObject;
  text?: string;
}

interface ParityCase extends JsonObject {
  id: string;
  initial?: InitialState;
  operations?: JsonObject[];
  expected?: JsonObject;
  samples?: Array<{ input: string; normalized: string; comparisonKey: string }>;
  generatedCommits?: { prefix: string; count: number; pad: number };
}

interface ParityContract {
  id: string;
  kind: 'search-history-identity' | 'search-history-document' | 'album-document' | 'album-operations';
  cases: ParityCase[];
}

interface ParityRegistry {
  schemaVersion: number;
  sourceOfTruth: string;
  contracts: ParityContract[];
}

interface ParityReceipt {
  schemaVersion: number;
  runtime: 'browser';
  contractSha256: string;
  contractIds: string[];
  caseIds: string[];
  casesRun: number;
  failures: string[];
}

function isObject(value: unknown): value is JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function expandRoot<T>(value: T, root: string): T {
  if (typeof value === 'string') return value.replaceAll('${ROOT}', root) as T;
  if (Array.isArray(value)) return value.map((entry) => expandRoot(entry, root)) as T;
  if (isObject(value)) {
    return Object.fromEntries(Object.entries(value).map(([key, entry]) => [key, expandRoot(entry, root)])) as T;
  }
  return value;
}

function check(failures: string[], condition: boolean, scope: string, message: string) {
  if (!condition) failures.push(`${scope}: ${message}`);
}

function checkEqual(failures: string[], actual: unknown, expected: unknown, scope: string, field: string) {
  check(
    failures,
    isDeepStrictEqual(actual, expected),
    scope,
    `${field} differed (actual ${JSON.stringify(actual)}, expected ${JSON.stringify(expected)})`,
  );
}

function checkUnknownFields(
  failures: string[],
  actual: unknown,
  expected: unknown,
  knownKeys: readonly string[],
  scope: string,
  field: string,
) {
  const actualObject = isObject(actual) ? actual : {};
  const expectedObject = isObject(expected) ? expected : {};
  const known = new Set(knownKeys);
  const actualUnknown = Object.fromEntries(Object.entries(actualObject).filter(([key]) => !known.has(key)));
  checkEqual(failures, actualUnknown, expectedObject, scope, field);
}

async function readBytesOrNull(target: string) {
  return fs.readFile(target).catch((error: NodeJS.ErrnoException) => {
    if (error.code === 'ENOENT') return null;
    throw error;
  });
}

async function materializeInitial(target: string, initial: InitialState, root: string) {
  if (initial.mode === 'missing') return;
  await fs.mkdir(path.dirname(target), { recursive: true });
  if (initial.mode === 'raw') {
    await fs.writeFile(target, expandRoot(initial.text ?? '', root), 'utf8');
    return;
  }
  await fs.writeFile(target, `${JSON.stringify(expandRoot(initial.document ?? {}, root), null, 2)}\n`, 'utf8');
}

function searchStatus(result: Awaited<ReturnType<typeof mutateSearchHistory>>) {
  if (result.ok) return 'Succeeded';
  if (result.malformed || result.futureVersion) return 'Protected';
  return 'Failed';
}

function albumStatus(result: Awaited<ReturnType<typeof mutateAlbums>>) {
  if (result.ok) return 'Succeeded';
  if (result.conflict) return 'Conflict';
  if (result.notFound) return 'NotFound';
  if (result.malformed || result.futureVersion) return 'Protected';
  return 'Invalid';
}

function expectedStatuses(expected: JsonObject) {
  const value = expected.statuses;
  if (Array.isArray(value)) return value.map(String);
  if (isObject(value) && typeof value.all === 'string' && Number.isSafeInteger(value.count)) {
    return Array.from({ length: Number(value.count) }, () => String(value.all));
  }
  return [];
}

function checkSearchEntries(
  failures: string[],
  actual: string[],
  expected: JsonObject,
  scope: string,
  source: string,
) {
  if (Array.isArray(expected.entries)) {
    checkEqual(failures, actual, expected.entries, scope, `${source}.entries`);
  }
  if (isObject(expected.entryWindow)) {
    checkEqual(failures, actual.length, expected.entryWindow.count, scope, `${source}.entryWindow.count`);
    checkEqual(failures, actual[0], expected.entryWindow.first, scope, `${source}.entryWindow.first`);
    checkEqual(failures, actual.at(-1), expected.entryWindow.last, scope, `${source}.entryWindow.last`);
  }
}

async function runSearchIdentity(contract: ParityContract, failures: string[]) {
  for (const vector of contract.cases) {
    const scope = `${contract.id}/${vector.id}`;
    for (const [index, sample] of (vector.samples ?? []).entries()) {
      checkEqual(failures, normalizeSearchHistoryQuery(sample.input), sample.normalized, scope, `samples[${index}].normalized`);
      checkEqual(failures, searchHistoryComparisonKey(sample.input), sample.comparisonKey, scope, `samples[${index}].comparisonKey`);
    }
  }
}

async function runSearchDocument(contract: ParityContract, suiteRoot: string, failures: string[]) {
  for (const vector of contract.cases) {
    const scope = `${contract.id}/${vector.id}`;
    const root = path.join(suiteRoot, contract.id, vector.id);
    const target = path.join(root, 'search-history.json');
    const initial = vector.initial ?? { mode: 'missing' };
    const expected = vector.expected ?? {};
    await fs.mkdir(root, { recursive: true });
    await materializeInitial(target, initial, root);
    const bytesBefore = await readBytesOrNull(target);
    const initialRead = await readSearchHistory(target);

    checkEqual(failures, initialRead.ok, expected.initialSupported, scope, 'initialSupported');
    checkEqual(failures, initialRead.malformed, expected.initialMalformed, scope, 'initialMalformed');
    checkEqual(failures, initialRead.futureVersion, expected.initialFutureVersion, scope, 'initialFutureVersion');

    const operations: SearchHistoryMutation[] = [];
    for (const operation of vector.operations ?? []) {
      if (operation.action === 'clear') operations.push({ action: 'clear' });
      else if ((operation.action === 'commit' || operation.action === 'delete') && typeof operation.query === 'string') {
        operations.push({ action: operation.action, query: operation.query });
      } else {
        failures.push(`${scope}: unsupported Search History operation`);
      }
    }
    if (vector.generatedCommits) {
      for (let index = 0; index < vector.generatedCommits.count; index += 1) {
        operations.push({
          action: 'commit',
          query: `${vector.generatedCommits.prefix}${String(index).padStart(vector.generatedCommits.pad, '0')}`,
        });
      }
    }

    const statuses: string[] = [];
    let latestEntries = initialRead.ok ? initialRead.entries : [];
    for (const operation of operations) {
      const result = await mutateSearchHistory(target, operation);
      statuses.push(searchStatus(result));
      latestEntries = result.entries;
    }
    checkEqual(failures, statuses, expectedStatuses(expected), scope, 'statuses');

    checkSearchEntries(failures, latestEntries, expected, scope, 'mutationResult');
    const finalRead = await readSearchHistory(target);
    checkEqual(failures, finalRead.ok, expected.finalSupported, scope, 'finalSupported');
    checkEqual(failures, finalRead.malformed, expected.finalMalformed, scope, 'finalMalformed');
    checkEqual(failures, finalRead.futureVersion, expected.finalFutureVersion, scope, 'finalFutureVersion');
    checkSearchEntries(failures, finalRead.entries, expected, scope, 'persisted');

    const bytesAfter = await readBytesOrNull(target);
    checkEqual(failures, bytesAfter !== null, expected.fileExists, scope, 'fileExists');
    const actualBytesUnchanged = Buffer.compare(bytesBefore ?? Buffer.alloc(0), bytesAfter ?? Buffer.alloc(0)) === 0;
    checkEqual(failures, actualBytesUnchanged, expected.bytesUnchanged, scope, 'bytesUnchanged');
    const stored = finalRead.ok && bytesAfter ? JSON.parse(bytesAfter.toString('utf8')) : {};
    checkUnknownFields(
      failures,
      stored,
      expected.unknownRoot,
      ['version', 'entries', 'updatedAtUtc'],
      scope,
      'unknownRoot',
    );
  }
}

function toAlbumMutation(operation: JsonObject, root: string): AlbumMutation | null {
  const expanded = expandRoot(operation, root);
  if (expanded.action === 'create' && typeof expanded.name === 'string') {
    return {
      action: 'create',
      name: expanded.name,
      albumId: typeof expanded.albumId === 'string' ? expanded.albumId : undefined,
      expectedRevision: typeof expanded.expectedRevision === 'number' ? expanded.expectedRevision : undefined,
    };
  }
  if (expanded.action === 'update' && typeof expanded.albumId === 'string') {
    return {
      action: 'update',
      albumId: expanded.albumId,
      name: typeof expanded.name === 'string' ? expanded.name : undefined,
      pinned: typeof expanded.pinned === 'boolean' ? expanded.pinned : undefined,
      coverMemberId: expanded.coverMemberId === null || typeof expanded.coverMemberId === 'string' ? expanded.coverMemberId : undefined,
      expectedRevision: typeof expanded.expectedRevision === 'number' ? expanded.expectedRevision : undefined,
    };
  }
  if (expanded.action === 'add' && typeof expanded.albumId === 'string' && Array.isArray(expanded.paths)) {
    return {
      action: 'add',
      albumId: expanded.albumId,
      paths: expanded.paths.map(String),
      expectedRevision: typeof expanded.expectedRevision === 'number' ? expanded.expectedRevision : undefined,
    };
  }
  if (expanded.action === 'cleanupPaths' && Array.isArray(expanded.paths)) {
    return {
      action: 'cleanupPaths',
      paths: expanded.paths.map(String),
      expectedRevision: typeof expanded.expectedRevision === 'number' ? expanded.expectedRevision : undefined,
    };
  }
  return null;
}

async function runAlbumContract(contract: ParityContract, suiteRoot: string, failures: string[]) {
  for (const vector of contract.cases) {
    const scope = `${contract.id}/${vector.id}`;
    const root = path.join(suiteRoot, contract.id, vector.id);
    const target = path.join(root, 'albums.json');
    const initial = vector.initial ?? { mode: 'missing' };
    const expected = expandRoot(vector.expected ?? {}, root);
    await fs.mkdir(root, { recursive: true });
    await materializeInitial(target, initial, root);
    const bytesBefore = await readBytesOrNull(target);
    const initialRead = await readAlbums(target);
    const bytesAfterRead = await readBytesOrNull(target);

    checkEqual(failures, initialRead.ok, expected.initialSupported, scope, 'initialSupported');
    checkEqual(failures, initialRead.exists, expected.initialExists, scope, 'initialExists');
    checkEqual(failures, initialRead.malformed, expected.initialMalformed, scope, 'initialMalformed');
    checkEqual(failures, initialRead.futureVersion, expected.initialFutureVersion, scope, 'initialFutureVersion');
    checkEqual(failures, initialRead.ok ? initialRead.document.revision : null, expected.initialRevision, scope, 'initialRevision');
    checkEqual(failures, initialRead.ok ? initialRead.document.albums.length : null, expected.initialAlbumCount, scope, 'initialAlbumCount');
    const actualBytesUnchangedAfterRead = Buffer.compare(bytesBefore ?? Buffer.alloc(0), bytesAfterRead ?? Buffer.alloc(0)) === 0;
    checkEqual(
      failures,
      actualBytesUnchangedAfterRead,
      expected.bytesUnchangedAfterRead,
      scope,
      'bytesUnchangedAfterRead',
    );

    const statuses: string[] = [];
    const changed: boolean[] = [];
    const revisions: Array<number | null> = [];
    for (const operation of vector.operations ?? []) {
      const mutation = toAlbumMutation(operation, root);
      if (!mutation) {
        failures.push(`${scope}: unsupported Album operation`);
        continue;
      }
      const result = await mutateAlbums(target, mutation);
      statuses.push(albumStatus(result));
      changed.push(result.changed);
      revisions.push(result.document?.revision ?? null);
    }
    checkEqual(failures, statuses, expectedStatuses(expected), scope, 'statuses');
    if (Array.isArray(expected.changed)) checkEqual(failures, changed, expected.changed, scope, 'changed');
    if (Array.isArray(expected.revisions)) checkEqual(failures, revisions, expected.revisions, scope, 'revisions');

    const finalRead = await readAlbums(target);
    checkEqual(failures, finalRead.ok ? finalRead.document.revision : null, expected.finalRevision, scope, 'finalRevision');
    checkEqual(failures, finalRead.ok ? finalRead.document.albums.length : null, expected.finalAlbumCount ?? (finalRead.ok ? finalRead.document.albums.length : null), scope, 'finalAlbumCount');
    const bytesAfterOperations = await readBytesOrNull(target);
    checkEqual(failures, bytesAfterOperations !== null, expected.fileExists ?? (bytesAfterOperations !== null), scope, 'fileExists');
    const actualBytesUnchangedAfterOperations = Buffer.compare(bytesBefore ?? Buffer.alloc(0), bytesAfterOperations ?? Buffer.alloc(0)) === 0;
    checkEqual(
      failures,
      actualBytesUnchangedAfterOperations,
      expected.bytesUnchangedAfterOperations,
      scope,
      'bytesUnchangedAfterOperations',
    );

    const stored = finalRead.ok && bytesAfterOperations
      ? JSON.parse(bytesAfterOperations.toString('utf8')) as JsonObject
      : {};
    checkUnknownFields(
      failures,
      stored,
      expected.unknownRoot,
      ['version', 'revision', 'updatedAtUtc', 'albums', 'recentAlbumIds'],
      scope,
      'unknownRoot',
    );
    if (bytesAfterOperations && finalRead.ok && isObject(expected.finalAlbum)) {
      const expectedAlbum = expected.finalAlbum;
      const finalAlbum = finalRead.document.albums.find((album) => album.id === expectedAlbum.id);
      check(failures, Boolean(finalAlbum), scope, 'final Album was missing');
      if (finalAlbum) {
        for (const key of ['id', 'name', 'pinned', 'coverMemberId', 'revision'] as const) {
          checkEqual(failures, finalAlbum[key], expectedAlbum[key], scope, `finalAlbum.${key}`);
        }
        const expectedPaths = Array.isArray(expectedAlbum.memberPaths)
          ? expectedAlbum.memberPaths.map((entry) => path.resolve(String(entry)))
          : [];
        checkEqual(failures, finalAlbum.members.map((member) => path.resolve(member.imagePath)), expectedPaths, scope, 'finalAlbum.memberPaths');
        checkUnknownFields(
          failures,
          finalAlbum,
          expected.unknownAlbum,
          ['id', 'name', 'pinned', 'coverMemberId', 'createdAtUtc', 'updatedAtUtc', 'revision', 'members'],
          scope,
          'unknownAlbum',
        );
        if (isObject(expected.unknownMember) && typeof expected.unknownMember.memberId === 'string') {
          const expectedMember = expected.unknownMember;
          const member = finalAlbum.members.find((candidate) => candidate.id === expectedMember.memberId);
          check(failures, Boolean(member), scope, 'unknown-field member was missing');
          if (member) {
            checkUnknownFields(
              failures,
              member,
              expectedMember.fields,
              ['id', 'imagePath', 'addedAtUtc'],
              scope,
              'unknownMember.fields',
            );
          }
        }
      }
    }
  }
}

async function writeReceipt(receipt: ParityReceipt) {
  const target = process.env.PVU_PARITY_BROWSER_RECEIPT_PATH;
  if (!target) return;
  const fullTarget = path.resolve(target);
  const tempRoot = path.resolve(os.tmpdir());
  if (fullTarget !== tempRoot && !fullTarget.startsWith(`${tempRoot}${path.sep}`)) {
    throw new Error(`Browser parity receipt must stay under TEMP: ${fullTarget}`);
  }
  await fs.mkdir(path.dirname(fullTarget), { recursive: true });
  await fs.writeFile(fullTarget, `${JSON.stringify(receipt, null, 2)}\n`, 'utf8');
}

it('matches the shared Browser/WPF parity vectors with Browser production logic', async () => {
  const repositoryRoot = process.cwd();
  const contractPath = path.join(repositoryRoot, 'contracts', 'parity-v1.json');
  const contractBytes = await fs.readFile(contractPath);
  const canonicalContractBytes = canonicalizeParityContractBytes(contractBytes);
  const registry = JSON.parse(canonicalContractBytes.toString('utf8')) as ParityRegistry;
  const contractIds = registry.contracts.map((contract) => contract.id);
  const caseIds = registry.contracts.flatMap((contract) => contract.cases.map((vector) => `${contract.id}/${vector.id}`));
  const failures: string[] = [];
  const suiteRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'pvu-browser-parity-v1-'));
  let casesRun = 0;

  try {
    check(failures, registry.schemaVersion === 1, 'registry', `unsupported schemaVersion ${registry.schemaVersion}`);
    for (const contract of registry.contracts) {
      const before = failures.length;
      try {
        if (contract.kind === 'search-history-identity') await runSearchIdentity(contract, failures);
        else if (contract.kind === 'search-history-document') await runSearchDocument(contract, suiteRoot, failures);
        else if (contract.kind === 'album-document' || contract.kind === 'album-operations') await runAlbumContract(contract, suiteRoot, failures);
        else failures.push(`${contract.id}: unsupported contract kind`);
      } catch (error) {
        failures.push(`${contract.id}: ${error instanceof Error ? error.message : String(error)}`);
      }
      casesRun += contract.cases.length;
      if (failures.length > before) continue;
    }
  } finally {
    const receipt: ParityReceipt = {
      schemaVersion: registry.schemaVersion,
      runtime: 'browser',
      contractSha256: parityContractSha256(contractBytes),
      contractIds,
      caseIds,
      casesRun,
      failures,
    };
    await writeReceipt(receipt);
    await fs.rm(suiteRoot, { recursive: true, force: true });
  }

  expect(failures).toEqual([]);
});
