import {
  DEFAULT_THUMBNAIL_STATUS_BORDERS,
  THUMBNAIL_STATUS_BORDER_RAINBOW,
  type ThumbnailStatusBorderPreference,
  type ThumbnailStatusBorderSettings,
} from './types';

const HEX_COLOR_PATTERN = /^#[0-9a-f]{6}$/i;

function isObject(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function isValidPreference(
  value: unknown,
  { allowRainbow }: { allowRainbow: boolean },
): value is Partial<ThumbnailStatusBorderPreference> & Record<string, unknown> {
  if (!isObject(value)) return false;
  if (Object.hasOwn(value, 'enabled') && typeof value.enabled !== 'boolean') return false;
  if (Object.hasOwn(value, 'color')) {
    if (typeof value.color !== 'string') return false;
    if (!HEX_COLOR_PATTERN.test(value.color)
      && !(allowRainbow && value.color.toLowerCase() === THUMBNAIL_STATUS_BORDER_RAINBOW)) {
      return false;
    }
  }
  return true;
}

export function isValidThumbnailStatusBordersDocument(value: unknown): value is Record<string, unknown> {
  if (!isObject(value)) return false;
  if (Object.hasOwn(value, 'favorite') && !isValidPreference(value.favorite, { allowRainbow: false })) return false;
  if (Object.hasOwn(value, 'enhanced') && !isValidPreference(value.enhanced, { allowRainbow: true })) return false;
  return true;
}

function normalizePreference(
  value: unknown,
  fallback: ThumbnailStatusBorderPreference,
  { allowRainbow }: { allowRainbow: boolean },
): ThumbnailStatusBorderPreference {
  if (!isValidPreference(value, { allowRainbow })) return { ...fallback };
  const storedColor = typeof value.color === 'string' ? value.color.toLowerCase() : fallback.color;
  return {
    enabled: typeof value.enabled === 'boolean' ? value.enabled : fallback.enabled,
    // `rainbow` remains accepted so an older shared settings file is never
    // rejected, but v1 now presents every thumbnail with a stable solid ring.
    color: storedColor === THUMBNAIL_STATUS_BORDER_RAINBOW ? fallback.color : storedColor,
  };
}

export function normalizeThumbnailStatusBorders(value: unknown): ThumbnailStatusBorderSettings {
  const document = isValidThumbnailStatusBordersDocument(value) ? value : {};
  return {
    favorite: normalizePreference(
      document.favorite,
      DEFAULT_THUMBNAIL_STATUS_BORDERS.favorite,
      { allowRainbow: false },
    ),
    enhanced: normalizePreference(
      document.enhanced,
      DEFAULT_THUMBNAIL_STATUS_BORDERS.enhanced,
      { allowRainbow: true },
    ),
  };
}

export interface ThumbnailStatusBorderPresentation {
  favoriteColor: string | null;
  enhancedColor: string | null;
  enhancedRainbow: boolean;
}

export function getThumbnailStatusBorderPresentation({
  favorite,
  enhanced,
  settings,
}: {
  favorite: boolean;
  enhanced: boolean;
  settings: ThumbnailStatusBorderSettings;
}): ThumbnailStatusBorderPresentation {
  const showEnhanced = enhanced && settings.enhanced.enabled;
  return {
    favoriteColor: favorite && settings.favorite.enabled ? settings.favorite.color : null,
    enhancedColor: showEnhanced ? settings.enhanced.color : null,
    enhancedRainbow: false,
  };
}
