export const PREVIEW_TAB_STORAGE_KEY = 'pvu_preview_tabs';
export const MAX_PERSISTED_PREVIEW_TABS = 30;

export interface PersistedPreviewTabs {
  version: 1;
  tabIds: string[];
  activeId: string | null;
}

const EMPTY_PREVIEW_TABS: PersistedPreviewTabs = {
  version: 1,
  tabIds: [],
  activeId: null,
};

/**
 * Preview tab ids are image paths, never display labels or API URLs. Keeping
 * this check here makes the browser-only persistence boundary deliberately
 * stricter than the in-memory tab state.
 */
function isSupportedAbsoluteImagePath(value: unknown): value is string {
  if (typeof value !== 'string' || value.length === 0) return false;
  const isAbsolute = /^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/]+[^\\/]+[\\/]|\/)/.test(value);
  return isAbsolute && /\.(?:jpe?g|png|webp)$/i.test(value);
}

export function normalizePersistedPreviewTabs(value: unknown): PersistedPreviewTabs {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return { ...EMPTY_PREVIEW_TABS };
  }

  const stored = value as Partial<PersistedPreviewTabs>;
  if (stored.version !== 1 || !Array.isArray(stored.tabIds)
    || (stored.activeId !== null && typeof stored.activeId !== 'string')) {
    return { ...EMPTY_PREVIEW_TABS };
  }

  const seen = new Set<string>();
  const tabIds: string[] = [];
  for (const id of stored.tabIds) {
    if (!isSupportedAbsoluteImagePath(id)) return { ...EMPTY_PREVIEW_TABS };
    if (seen.has(id)) continue;
    seen.add(id);
    tabIds.push(id);
    if (tabIds.length === MAX_PERSISTED_PREVIEW_TABS) break;
  }

  return {
    version: 1,
    tabIds,
    activeId: stored.activeId && seen.has(stored.activeId)
      ? stored.activeId
      : tabIds[0] ?? null,
  };
}

export function serializePersistedPreviewTabs(
  tabIds: readonly string[],
  activeId: string | null,
): PersistedPreviewTabs {
  return normalizePersistedPreviewTabs({ version: 1, tabIds, activeId });
}
