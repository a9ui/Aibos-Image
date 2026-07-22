export function parseArgs(argv) {
  const args = argv.slice(2);
  const parsed = { _: [] };

  for (let index = 0; index < args.length; index += 1) {
    const token = args[index];
    if (!token.startsWith('--')) {
      parsed._.push(token);
      continue;
    }

    const eqIndex = token.indexOf('=');
    if (eqIndex !== -1) {
      const key = token.slice(2, eqIndex);
      parsed[key] = token.slice(eqIndex + 1);
      continue;
    }

    const key = token.slice(2);
    const next = args[index + 1];
    if (next && !next.startsWith('--')) {
      parsed[key] = next;
      index += 1;
    } else {
      parsed[key] = true;
    }
  }

  return parsed;
}

export function requireArg(parsed, key) {
  const value = parsed[key];
  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`Missing required argument: --${key}`);
  }
  return value;
}
