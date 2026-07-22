import { createHash } from 'node:crypto';

export function canonicalizeParityContractBytes(bytes) {
  const text = Buffer.from(bytes).toString('utf8');
  const withoutCrlf = text.replaceAll('\r\n', '');
  if (withoutCrlf.includes('\r')) {
    throw new Error('Parity contract contains a bare carriage return');
  }
  return Buffer.from(text.replaceAll('\r\n', '\n'), 'utf8');
}

export function parityContractSha256(bytes) {
  return createHash('sha256').update(canonicalizeParityContractBytes(bytes)).digest('hex');
}
