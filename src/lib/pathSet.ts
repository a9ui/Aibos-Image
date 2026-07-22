export function parseDirSet(value?: string | null): string[] {
  if (!value) return [];
  const seen = new Set<string>();
  const paths: string[] = [];

  for (const raw of value.split(/\r?\n/)) {
    const item = raw.trim();
    if (!item) continue;
    const key = item.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    paths.push(item);
  }

  return paths;
}

export function formatDirSet(paths: string[]): string {
  return parseDirSet(paths.join('\n')).join('\n');
}

export function appendDirSet(current: string | null | undefined, additions: string[] | string): string {
  const incoming = Array.isArray(additions) ? additions : parseDirSet(additions);
  return formatDirSet([...parseDirSet(current), ...incoming]);
}

export function removeFromDirSet(current: string | null | undefined, target: string): string {
  const targetKey = target.trim().toLowerCase();
  if (!targetKey) return formatDirSet(parseDirSet(current));
  return formatDirSet(parseDirSet(current).filter((item) => item.toLowerCase() !== targetKey));
}

export function countDirSet(value?: string | null): number {
  return parseDirSet(value).length;
}

export function isDirSet(value?: string | null): boolean {
  return countDirSet(value) > 1;
}

export function basenameFromPath(value: string): string {
  const trimmed = value.trim().replace(/[\\/]+$/, '');
  if (!trimmed) return '';
  const parts = trimmed.split(/[\\/]+/).filter(Boolean);
  return parts[parts.length - 1] || trimmed;
}

export function summarizeDirSet(value?: string | null): string {
  const paths = parseDirSet(value);
  if (paths.length === 0) return '';
  if (paths.length === 1) return basenameFromPath(paths[0]) || paths[0];
  const first = basenameFromPath(paths[0]) || paths[0];
  return `${paths.length} folders: ${first}${paths.length > 1 ? ' ...' : ''}`;
}
