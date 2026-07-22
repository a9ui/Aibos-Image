import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { canonicalizeParityContractBytes, parityContractSha256 } from './parity-contract-hash.mjs';

const CONTRACT_ID_PATTERN = /^PV-[A-Z0-9]+(?:-[A-Z0-9]+)*-\d{3}$/;
const DOCUMENT_CONTRACT_ID_PATTERN = /PV-[A-Z0-9]+(?:-[A-Z0-9]+)*-\d{3}/g;
const CASE_ID_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const ALLOWED_KINDS = new Set([
  'search-history-identity',
  'search-history-document',
  'album-document',
  'album-operations',
]);

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function assertExactKeys(value, required, optional, field) {
  assert(isObject(value), `${field} must be an object`);
  const allowed = new Set([...required, ...optional]);
  for (const key of Object.keys(value)) assert(allowed.has(key), `${field} has unknown property ${key}`);
  for (const key of required) assert(Object.hasOwn(value, key), `${field} is missing ${key}`);
}

function assertString(value, field) {
  assert(typeof value === 'string', `${field} must be a string`);
}

function assertBoolean(value, field) {
  assert(typeof value === 'boolean', `${field} must be a boolean`);
}

function assertNonNegativeInteger(value, field, maximum = Number.MAX_SAFE_INTEGER) {
  assert(Number.isSafeInteger(value) && value >= 0 && value <= maximum, `${field} must be a non-negative integer`);
}

function assertNullableNonNegativeInteger(value, field) {
  assert(value === null || (Number.isSafeInteger(value) && value >= 0), `${field} must be a non-negative integer or null`);
}

function assertStringArray(value, field, requireNonEmpty = false) {
  assert(Array.isArray(value) && (!requireNonEmpty || value.length > 0), `${field} must be ${requireNonEmpty ? 'a non-empty' : 'an'} array`);
  for (const [index, entry] of value.entries()) assertString(entry, `${field}[${index}]`);
}

function assertBooleanArray(value, field) {
  assert(Array.isArray(value), `${field} must be an array`);
  for (const [index, entry] of value.entries()) assertBoolean(entry, `${field}[${index}]`);
}

function assertIntegerArray(value, field) {
  assert(Array.isArray(value), `${field} must be an array`);
  for (const [index, entry] of value.entries()) assertNonNegativeInteger(entry, `${field}[${index}]`);
}

function occurrences(text, needle) {
  let count = 0;
  let offset = 0;
  while ((offset = text.indexOf(needle, offset)) >= 0) {
    count += 1;
    offset += needle.length;
  }
  return count;
}

function assertRelativeRepositoryPath(value, field) {
  assert(typeof value === 'string' && value.length > 0, `${field} must be a non-empty string`);
  assert(!path.isAbsolute(value), `${field} must be repository-relative`);
  assert(!value.includes('\\'), `${field} must use forward slashes`);
  const segments = value.split('/');
  assert(!segments.includes('') && !segments.includes('.') && !segments.includes('..'), `${field} has an unsafe segment`);
}

function validateInitial(initial, field) {
  assertExactKeys(initial, ['mode'], ['document', 'text'], field);
  assert(['missing', 'json', 'raw'].includes(initial.mode), `${field}.mode is unsupported`);
  if (initial.mode === 'missing') assertExactKeys(initial, ['mode'], [], field);
  if (initial.mode === 'json') {
    assertExactKeys(initial, ['mode', 'document'], [], field);
    assert(isObject(initial.document), `${field}.document must be an object`);
  }
  if (initial.mode === 'raw') {
    assertExactKeys(initial, ['mode', 'text'], [], field);
    assertString(initial.text, `${field}.text`);
  }
}

function validateStatuses(value, field) {
  if (Array.isArray(value)) {
    assertStringArray(value, field);
    return;
  }
  assertExactKeys(value, ['all', 'count'], [], field);
  assertString(value.all, `${field}.all`);
  assertNonNegativeInteger(value.count, `${field}.count`, 100);
}

function validateSearchOperations(operations, field) {
  assert(Array.isArray(operations), `${field} must be an array`);
  for (const [index, operation] of operations.entries()) {
    const operationField = `${field}[${index}]`;
    assertExactKeys(operation, ['action'], ['query'], operationField);
    if (operation.action === 'clear') assertExactKeys(operation, ['action'], [], operationField);
    else if (operation.action === 'commit' || operation.action === 'delete') {
      assertExactKeys(operation, ['action', 'query'], [], operationField);
      assertString(operation.query, `${operationField}.query`);
    } else {
      throw new Error(`${operationField}.action is unsupported`);
    }
  }
}

function validateSearchExpected(expected, field) {
  assertExactKeys(
    expected,
    [
      'initialSupported', 'initialMalformed', 'initialFutureVersion',
      'finalSupported', 'finalMalformed', 'finalFutureVersion', 'fileExists',
      'statuses', 'unknownRoot', 'bytesUnchanged',
    ],
    ['entries', 'entryWindow'],
    field,
  );
  for (const key of [
    'initialSupported', 'initialMalformed', 'initialFutureVersion',
    'finalSupported', 'finalMalformed', 'finalFutureVersion', 'fileExists',
    'bytesUnchanged',
  ]) {
    assertBoolean(expected[key], `${field}.${key}`);
  }
  validateStatuses(expected.statuses, `${field}.statuses`);
  assert(isObject(expected.unknownRoot), `${field}.unknownRoot must be an object`);
  const hasEntries = Object.hasOwn(expected, 'entries');
  const hasWindow = Object.hasOwn(expected, 'entryWindow');
  assert(hasEntries !== hasWindow, `${field} must contain exactly one of entries or entryWindow`);
  if (hasEntries) assertStringArray(expected.entries, `${field}.entries`);
  if (hasWindow) {
    assertExactKeys(expected.entryWindow, ['count', 'first', 'last'], [], `${field}.entryWindow`);
    assertNonNegativeInteger(expected.entryWindow.count, `${field}.entryWindow.count`, 50);
    assertString(expected.entryWindow.first, `${field}.entryWindow.first`);
    assertString(expected.entryWindow.last, `${field}.entryWindow.last`);
  }
}

function validateSearchHistoryIdentity(contract) {
  for (const [caseIndex, vector] of contract.cases.entries()) {
    assertExactKeys(vector, ['id', 'samples'], [], `${contract.id}.cases[${caseIndex}]`);
    assert(Array.isArray(vector.samples) && vector.samples.length > 0, `${contract.id}.cases[${caseIndex}].samples must be non-empty`);
    for (const [sampleIndex, sample] of vector.samples.entries()) {
      const field = `${contract.id}.cases[${caseIndex}].samples[${sampleIndex}]`;
      assertExactKeys(sample, ['input', 'normalized', 'comparisonKey'], [], field);
      for (const key of ['input', 'normalized', 'comparisonKey']) {
        assertString(sample[key], `${field}.${key}`);
      }
    }
  }
}

function validateSearchHistoryDocument(contract) {
  for (const [caseIndex, vector] of contract.cases.entries()) {
    const field = `${contract.id}.cases[${caseIndex}]`;
    assertExactKeys(vector, ['id', 'initial', 'expected'], ['operations', 'generatedCommits'], field);
    validateInitial(vector.initial, `${field}.initial`);
    if (vector.operations !== undefined) validateSearchOperations(vector.operations, `${field}.operations`);
    if (vector.generatedCommits !== undefined) {
      const generated = vector.generatedCommits;
      assertExactKeys(generated, ['prefix', 'count', 'pad'], [], `${field}.generatedCommits`);
      assertString(generated.prefix, `${field}.generatedCommits.prefix`);
      assertNonNegativeInteger(generated.count, `${field}.generatedCommits.count`, 100);
      assert(generated.count > 0, `${field}.generatedCommits.count must be positive`);
      assertNonNegativeInteger(generated.pad, `${field}.generatedCommits.pad`, 8);
    }
    validateSearchExpected(vector.expected, `${field}.expected`);
  }
}

function validateAlbumDocumentOperations(operations, field) {
  assert(Array.isArray(operations), `${field} must be an array`);
  for (const [index, operation] of operations.entries()) {
    const operationField = `${field}[${index}]`;
    assertExactKeys(operation, ['action', 'name', 'albumId'], ['expectedRevision'], operationField);
    assert(operation.action === 'create', `${operationField}.action must be create`);
    assertString(operation.name, `${operationField}.name`);
    assertString(operation.albumId, `${operationField}.albumId`);
    if (Object.hasOwn(operation, 'expectedRevision')) assertNonNegativeInteger(operation.expectedRevision, `${operationField}.expectedRevision`);
  }
}

function validateAlbumOperations(operations, field) {
  assert(Array.isArray(operations) && operations.length > 0, `${field} must be a non-empty array`);
  for (const [index, operation] of operations.entries()) {
    const operationField = `${field}[${index}]`;
    assertExactKeys(operation, ['action'], ['albumId', 'paths', 'name', 'pinned', 'expectedRevision'], operationField);
    if (operation.action === 'add') {
      assertExactKeys(operation, ['action', 'albumId', 'paths'], ['expectedRevision'], operationField);
      assertString(operation.albumId, `${operationField}.albumId`);
      assertStringArray(operation.paths, `${operationField}.paths`, true);
    } else if (operation.action === 'update') {
      assertExactKeys(operation, ['action', 'albumId'], ['name', 'pinned', 'expectedRevision'], operationField);
      assertString(operation.albumId, `${operationField}.albumId`);
      if (Object.hasOwn(operation, 'name')) assertString(operation.name, `${operationField}.name`);
      if (Object.hasOwn(operation, 'pinned')) assertBoolean(operation.pinned, `${operationField}.pinned`);
    } else if (operation.action === 'cleanupPaths') {
      assertExactKeys(operation, ['action', 'paths'], ['expectedRevision'], operationField);
      assertStringArray(operation.paths, `${operationField}.paths`, true);
    } else {
      throw new Error(`${operationField}.action is unsupported`);
    }
    if (Object.hasOwn(operation, 'expectedRevision')) assertNonNegativeInteger(operation.expectedRevision, `${operationField}.expectedRevision`);
  }
}

function validateAlbumBaseExpected(expected, field, operationsKind) {
  const baseKeys = [
    'initialSupported', 'initialExists', 'initialMalformed', 'initialFutureVersion',
    'initialRevision', 'initialAlbumCount', 'statuses', 'finalRevision', 'finalAlbumCount',
    'fileExists', 'bytesUnchangedAfterRead', 'bytesUnchangedAfterOperations', 'unknownRoot',
  ];
  const operationKeys = ['changed', 'revisions', 'finalAlbum', 'unknownAlbum', 'unknownMember'];
  assertExactKeys(expected, operationsKind ? [...baseKeys, ...operationKeys] : baseKeys, [], field);
  for (const key of [
    'initialSupported', 'initialExists', 'initialMalformed', 'initialFutureVersion',
    'fileExists', 'bytesUnchangedAfterRead', 'bytesUnchangedAfterOperations',
  ]) assertBoolean(expected[key], `${field}.${key}`);
  assertNullableNonNegativeInteger(expected.initialRevision, `${field}.initialRevision`);
  assertNullableNonNegativeInteger(expected.initialAlbumCount, `${field}.initialAlbumCount`);
  assertStringArray(expected.statuses, `${field}.statuses`);
  assertNullableNonNegativeInteger(expected.finalRevision, `${field}.finalRevision`);
  assertNullableNonNegativeInteger(expected.finalAlbumCount, `${field}.finalAlbumCount`);
  assert(isObject(expected.unknownRoot), `${field}.unknownRoot must be an object`);
  if (!operationsKind) return;

  assertBooleanArray(expected.changed, `${field}.changed`);
  assertIntegerArray(expected.revisions, `${field}.revisions`);
  assert(expected.finalRevision !== null && expected.finalAlbumCount !== null, `${field} final revision and count must be integers`);
  assertExactKeys(expected.finalAlbum, ['id', 'name', 'pinned', 'coverMemberId', 'revision', 'memberPaths'], [], `${field}.finalAlbum`);
  assertString(expected.finalAlbum.id, `${field}.finalAlbum.id`);
  assertString(expected.finalAlbum.name, `${field}.finalAlbum.name`);
  assertBoolean(expected.finalAlbum.pinned, `${field}.finalAlbum.pinned`);
  assert(expected.finalAlbum.coverMemberId === null || typeof expected.finalAlbum.coverMemberId === 'string', `${field}.finalAlbum.coverMemberId must be a string or null`);
  assertNonNegativeInteger(expected.finalAlbum.revision, `${field}.finalAlbum.revision`);
  assertStringArray(expected.finalAlbum.memberPaths, `${field}.finalAlbum.memberPaths`);
  assert(isObject(expected.unknownAlbum), `${field}.unknownAlbum must be an object`);
  assertExactKeys(expected.unknownMember, ['memberId', 'fields'], [], `${field}.unknownMember`);
  assertString(expected.unknownMember.memberId, `${field}.unknownMember.memberId`);
  assert(isObject(expected.unknownMember.fields), `${field}.unknownMember.fields must be an object`);
}

function validateAlbumContract(contract) {
  for (const [caseIndex, vector] of contract.cases.entries()) {
    const field = `${contract.id}.cases[${caseIndex}]`;
    assertExactKeys(vector, ['id', 'initial', 'operations', 'expected'], [], field);
    validateInitial(vector.initial, `${field}.initial`);
    if (contract.kind === 'album-document') validateAlbumDocumentOperations(vector.operations, `${field}.operations`);
    else validateAlbumOperations(vector.operations, `${field}.operations`);
    validateAlbumBaseExpected(vector.expected, `${field}.expected`, contract.kind === 'album-operations');
  }
}

export function validateParityRegistry(registry, normativeText) {
  assertExactKeys(registry, ['schemaVersion', 'sourceOfTruth', 'contracts'], [], 'contract root');
  assert(registry.schemaVersion === 1, `unsupported schemaVersion: ${String(registry.schemaVersion)}`);
  assertRelativeRepositoryPath(registry.sourceOfTruth, 'sourceOfTruth');
  assert(registry.sourceOfTruth === 'docs/product-contract.md', 'sourceOfTruth must be docs/product-contract.md');
  assert(Array.isArray(registry.contracts) && registry.contracts.length > 0, 'contracts must be a non-empty array');

  const ids = new Set();
  const caseIds = new Set();
  for (const [contractIndex, contract] of registry.contracts.entries()) {
    const field = `contracts[${contractIndex}]`;
    assertExactKeys(contract, ['id', 'kind', 'cases'], [], field);
    assert(typeof contract.id === 'string' && CONTRACT_ID_PATTERN.test(contract.id), `${field}.id is invalid`);
    assert(!ids.has(contract.id), `duplicate contract id: ${contract.id}`);
    ids.add(contract.id);
    assert(ALLOWED_KINDS.has(contract.kind), `${contract.id} has unsupported kind: ${String(contract.kind)}`);
    assert(Array.isArray(contract.cases) && contract.cases.length > 0, `${contract.id}.cases must be non-empty`);
    for (const [caseIndex, vector] of contract.cases.entries()) {
      assert(isObject(vector), `${contract.id}.cases[${caseIndex}] must be an object`);
      assert(typeof vector.id === 'string' && CASE_ID_PATTERN.test(vector.id), `${contract.id}.cases[${caseIndex}].id is invalid`);
      const qualified = `${contract.id}/${vector.id}`;
      assert(!caseIds.has(qualified), `duplicate parity case id: ${qualified}`);
      caseIds.add(qualified);
    }

    if (contract.kind === 'search-history-identity') validateSearchHistoryIdentity(contract);
    else if (contract.kind === 'search-history-document') validateSearchHistoryDocument(contract);
    else validateAlbumContract(contract);

    assert(occurrences(normativeText, contract.id) === 1, `${contract.id} must appear exactly once in ${registry.sourceOfTruth}`);
  }

  const documentedIds = new Set(normativeText.match(DOCUMENT_CONTRACT_ID_PATTERN) ?? []);
  for (const id of documentedIds) assert(ids.has(id), `documented contract id is missing from the executable registry: ${id}`);
  for (const id of ids) assert(documentedIds.has(id), `registry contract id is missing from the normative document: ${id}`);

  return {
    schemaVersion: registry.schemaVersion,
    contractIds: [...ids],
    caseIds: [...caseIds],
    contracts: ids.size,
    cases: caseIds.size,
  };
}

export async function verifyParityRepository(repositoryRoot) {
  const contractPath = path.join(repositoryRoot, 'contracts', 'parity-v1.json');
  const bytes = await readFile(contractPath);
  const canonicalBytes = canonicalizeParityContractBytes(bytes);
  const registry = JSON.parse(canonicalBytes.toString('utf8'));
  assertRelativeRepositoryPath(registry.sourceOfTruth, 'sourceOfTruth');
  const normativePath = path.join(repositoryRoot, ...registry.sourceOfTruth.split('/'));
  const normative = await readFile(normativePath, 'utf8');
  assert((await stat(normativePath)).isFile(), 'sourceOfTruth must resolve to a file');
  const validated = validateParityRegistry(registry, normative);
  return {
    ok: true,
    registry: 'contracts/parity-v1.json',
    sourceOfTruth: registry.sourceOfTruth,
    sha256: parityContractSha256(bytes),
    ...validated,
  };
}

const invokedPath = process.argv[1] ? path.resolve(process.argv[1]) : '';
if (invokedPath === fileURLToPath(import.meta.url)) {
  const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  try {
    console.log(JSON.stringify(await verifyParityRepository(repositoryRoot), null, 2));
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  }
}
