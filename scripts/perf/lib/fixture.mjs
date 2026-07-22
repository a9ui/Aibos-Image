import fs from 'node:fs';
import path from 'node:path';

const REQUIRED_MANIFEST_FIELDS = [
  'fileCount',
  'totalBytes',
  'formats',
  'maxDirectoryDepth',
  'malformedFileCount',
  'lockedFileCount',
];

function pendingFixture(reason) {
  return {
    status: 'pending_fixture',
    manifestPath: null,
    fileCount: null,
    totalBytes: null,
    dimensions: null,
    formats: null,
    maxDirectoryDepth: null,
    malformedFileCount: null,
    lockedFileCount: null,
    reason,
  };
}

export function loadFixtureManifest(manifestPath) {
  if (!manifestPath) {
    return pendingFixture(
      'No fixture manifest supplied. Provide --fixture-manifest when representative test folders are defined.',
    );
  }

  const resolvedPath = path.resolve(manifestPath);
  if (!fs.existsSync(resolvedPath)) {
    return pendingFixture(`Fixture manifest not found: ${resolvedPath}`);
  }

  let parsed;
  try {
    parsed = JSON.parse(fs.readFileSync(resolvedPath, 'utf8'));
  } catch (error) {
    return pendingFixture(`Fixture manifest is not valid JSON: ${error.message}`);
  }

  const missingFields = REQUIRED_MANIFEST_FIELDS.filter((field) => parsed[field] == null);
  if (missingFields.length > 0) {
    return pendingFixture(
      `Fixture manifest is missing required fields: ${missingFields.join(', ')}`,
    );
  }

  return {
    status: 'loaded',
    manifestPath: resolvedPath,
    fileCount: parsed.fileCount,
    totalBytes: parsed.totalBytes,
    dimensions: parsed.dimensions ?? null,
    formats: parsed.formats,
    maxDirectoryDepth: parsed.maxDirectoryDepth,
    malformedFileCount: parsed.malformedFileCount,
    lockedFileCount: parsed.lockedFileCount,
    notes: parsed.notes ?? null,
  };
}
