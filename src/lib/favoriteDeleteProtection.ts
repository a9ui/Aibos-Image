export interface FavoriteDeleteProtection {
  favoriteCount: number;
  requiresConfirmation: boolean;
}

export function getFavoriteDeleteProtection(
  targetIds: readonly string[],
  favorites: Readonly<Record<string, number>>
): FavoriteDeleteProtection {
  const uniqueIds = new Set(targetIds.filter((id) => id.trim().length > 0));
  let favoriteCount = 0;
  for (const id of uniqueIds) {
    if ((favorites[id] ?? 0) > 0) favoriteCount += 1;
  }
  return {
    favoriteCount,
    requiresConfirmation: favoriteCount > 0,
  };
}

export function shouldConfirmSourceDelete(
  confirmBeforeDelete: boolean,
  protection: FavoriteDeleteProtection
): boolean {
  return confirmBeforeDelete || protection.requiresConfirmation;
}
