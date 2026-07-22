const LEGACY_TO_UPSCALE_KEYS = [
  'favorites',
  'favorites_backup',
  'view',
  'pinned_tabs',
  'perf_enabled',
  'fav_only',
  'unfav_only',
  'scroll_memory',
  'seen_images',
  'recent_dirs',
  'last_dir_set',
];

export function migrateLegacyPhotoviewerState() {
  if (typeof window === 'undefined') return;

  try {
    for (const suffix of LEGACY_TO_UPSCALE_KEYS) {
      const nextKey = `pvu_${suffix}`;
      if (localStorage.getItem(nextKey) !== null) continue;
      const legacyValue = localStorage.getItem(`pv_${suffix}`);
      if (legacyValue !== null) {
        localStorage.setItem(nextKey, legacyValue);
      }
    }

    localStorage.setItem('pvu_legacy_imported', '1');
  } catch {
    // LocalStorage can be unavailable. The app can continue without migration.
  }
}
