import { describe, expect, it } from 'vitest';

import { canonicalizeParityContractBytes, parityContractSha256 } from './parity-contract-hash.mjs';
import { validateParityRegistry, verifyParityRepository } from './verify-parity-contracts.mjs';

function minimalRegistry() {
  return {
    schemaVersion: 1,
    sourceOfTruth: 'docs/product-contract.md',
    contracts: [{
      id: 'PV-SH-001',
      kind: 'search-history-identity',
      cases: [{
        id: 'one',
        samples: [{ input: 'A', normalized: 'A', comparisonKey: 'a' }],
      }],
    }],
  };
}

describe('parity contract registry verifier', () => {
  it('validates the repository registry and its normative IDs', async () => {
    await expect(verifyParityRepository(process.cwd())).resolves.toMatchObject({
      ok: true,
      schemaVersion: 1,
      contracts: 4,
      cases: 12,
    });
  });

  it('rejects a shared ID that is missing from the normative document', () => {
    expect(() => validateParityRegistry(minimalRegistry(), '# Product contract\n')).toThrow(/must appear exactly once/);
  });

  it('does not allow the registry to redirect the normative source', () => {
    const registry = minimalRegistry();
    registry.sourceOfTruth = 'README.md';
    expect(() => validateParityRegistry(registry, '`PV-SH-001`')).toThrow(/sourceOfTruth must be docs\/product-contract\.md/);
  });

  it('rejects a documented ID without an executable vector', () => {
    const registry = minimalRegistry();
    expect(() => validateParityRegistry(registry, '`PV-SH-001`\n`PV-ALB-999`')).toThrow(/missing from the executable registry/);
  });

  it('rejects duplicate qualified case IDs', () => {
    const registry = minimalRegistry();
    registry.contracts[0].cases.push({ ...registry.contracts[0].cases[0] });
    expect(() => validateParityRegistry(registry, '`PV-SH-001`')).toThrow(/duplicate parity case id/);
  });

  it('fails closed for an unknown contract kind or schema version', () => {
    const unknownKind = minimalRegistry();
    unknownKind.contracts[0].kind = 'renderer-guess';
    expect(() => validateParityRegistry(unknownKind, '`PV-SH-001`')).toThrow(/unsupported kind/);

    const future = minimalRegistry();
    future.schemaVersion = 2;
    expect(() => validateParityRegistry(future, '`PV-SH-001`')).toThrow(/unsupported schemaVersion/);
  });

  it('rejects unknown case fields, operations, and incomplete expectations', () => {
    const unknownCaseField = minimalRegistry();
    unknownCaseField.contracts[0].cases[0].rendererOnly = true;
    expect(() => validateParityRegistry(unknownCaseField, '`PV-SH-001`')).toThrow(/unknown property rendererOnly/);

    const document = minimalRegistry();
    document.contracts[0] = {
      id: 'PV-SH-001',
      kind: 'search-history-document',
      cases: [{
        id: 'one',
        initial: { mode: 'missing' },
        operations: [{ action: 'rendererGuess' }],
        expected: {
          initialSupported: true,
          initialMalformed: false,
          initialFutureVersion: false,
          finalSupported: true,
          finalMalformed: false,
          finalFutureVersion: false,
          fileExists: false,
          statuses: [],
          entries: [],
          unknownRoot: {},
          bytesUnchanged: true,
        },
      }],
    };
    expect(() => validateParityRegistry(document, '`PV-SH-001`')).toThrow(/action is unsupported/);

    document.contracts[0].cases[0].operations = [];
    delete document.contracts[0].cases[0].expected.bytesUnchanged;
    expect(() => validateParityRegistry(document, '`PV-SH-001`')).toThrow(/missing bytesUnchanged/);
  });

  it('uses one LF-canonical contract hash and rejects bare carriage returns', () => {
    const lf = Buffer.from('{\n  "schemaVersion": 1\n}\n', 'utf8');
    const crlf = Buffer.from('{\r\n  "schemaVersion": 1\r\n}\r\n', 'utf8');
    expect(canonicalizeParityContractBytes(crlf)).toEqual(lf);
    expect(parityContractSha256(crlf)).toBe(parityContractSha256(lf));
    expect(() => canonicalizeParityContractBytes(Buffer.from('{\r"schemaVersion":1}\n', 'utf8')))
      .toThrow(/bare carriage return/);
  });
});
