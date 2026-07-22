export const FAVORITE_FILTER_LEVELS = [1, 2, 3, 4, 5] as const;

export type FavoriteFilterLevel = (typeof FAVORITE_FILTER_LEVELS)[number];

export const DEFAULT_SHOW_UNSEEN_MARKERS = false;

function isFavoriteFilterLevel(value: unknown): value is FavoriteFilterLevel {
  return typeof value === 'number' && FAVORITE_FILTER_LEVELS.includes(value as FavoriteFilterLevel);
}

export function normalizeFavoriteFilterLevels(value: unknown): FavoriteFilterLevel[] {
  const values = Array.isArray(value) ? value : [value];
  return FAVORITE_FILTER_LEVELS.filter((level) => values.some((candidate) => candidate === level));
}

export function readStoredFavoriteFilterLevels(raw: string | null): FavoriteFilterLevel[] {
  if (!raw) return [];
  try {
    return normalizeFavoriteFilterLevels(JSON.parse(raw));
  } catch {
    const numeric = Number(raw);
    return isFavoriteFilterLevel(numeric) ? [numeric] : [];
  }
}

export function readFavoriteFilterLevelsPreference(
  currentRaw: string | null,
  legacyRaw: string | null
): FavoriteFilterLevel[] {
  return currentRaw !== null
    ? readStoredFavoriteFilterLevels(currentRaw)
    : readStoredFavoriteFilterLevels(legacyRaw);
}

export function toggleFavoriteFilterLevel(
  selectedLevels: FavoriteFilterLevel[],
  level: FavoriteFilterLevel
): FavoriteFilterLevel[] {
  return FAVORITE_FILTER_LEVELS.filter((candidate) => (
    candidate === level ? !selectedLevels.includes(candidate) : selectedLevels.includes(candidate)
  ));
}

export function matchesFavoriteLevel(
  imageLevel: number,
  selectedLevels: FavoriteFilterLevel[]
): boolean {
  if (selectedLevels.length === 0) return imageLevel > 0;
  return isFavoriteFilterLevel(imageLevel) && selectedLevels.includes(imageLevel);
}

export function isUnseenMarkerVisible(showUnseenMarkers: boolean, isSeen: boolean): boolean {
  return showUnseenMarkers && !isSeen;
}
