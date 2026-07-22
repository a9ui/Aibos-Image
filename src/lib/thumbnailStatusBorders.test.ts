import { describe, expect, it } from 'vitest';

import {
  DEFAULT_THUMBNAIL_STATUS_BORDERS,
} from './types';
import {
  getThumbnailStatusBorderPresentation,
  isValidThumbnailStatusBordersDocument,
  normalizeThumbnailStatusBorders,
} from './thumbnailStatusBorders';

describe('thumbnail status borders', () => {
  it('defaults independent indicators to favorite yellow and enhanced solid cyan', () => {
    expect(normalizeThumbnailStatusBorders(undefined)).toEqual(DEFAULT_THUMBNAIL_STATUS_BORDERS);
    expect(getThumbnailStatusBorderPresentation({
      favorite: true,
      enhanced: true,
      settings: normalizeThumbnailStatusBorders(undefined),
    })).toEqual({
      favoriteColor: '#facc15',
      enhancedColor: '#38bdf8',
      enhancedRainbow: false,
    });
  });

  it('keeps disabled indicators off without hiding the other status', () => {
    const settings = normalizeThumbnailStatusBorders({
      favorite: { enabled: false, color: '#ff0000' },
      enhanced: { enabled: true, color: '#00ff00' },
    });
    expect(getThumbnailStatusBorderPresentation({ favorite: true, enhanced: true, settings }))
      .toEqual({ favoriteColor: null, enhancedColor: '#00ff00', enhancedRainbow: false });

    const favoriteOnlySettings = normalizeThumbnailStatusBorders({
      favorite: { enabled: true, color: '#ff0000' },
      enhanced: { enabled: false, color: 'rainbow' },
    });
    expect(getThumbnailStatusBorderPresentation({
      favorite: true,
      enhanced: true,
      settings: favoriteOnlySettings,
    })).toEqual({ favoriteColor: '#ff0000', enhancedColor: null, enhancedRainbow: false });
  });

  it('normalizes custom colors and uses solid cyan for a missing enhanced preference', () => {
    expect(normalizeThumbnailStatusBorders({
      favorite: { color: '#ABCDEF' },
      futureStatus: { enabled: true },
    })).toEqual({
      favorite: { enabled: true, color: '#abcdef' },
      enhanced: { enabled: true, color: '#38bdf8' },
    });
  });

  it('keeps existing enhanced hex colors solid and safely migrates a legacy rainbow token in memory', () => {
    expect(normalizeThumbnailStatusBorders({
      enhanced: { enabled: true, color: '#ABCDEF' },
    }).enhanced).toEqual({ enabled: true, color: '#abcdef' });
    expect(normalizeThumbnailStatusBorders({
      enhanced: { enabled: false, color: 'RAINBOW' },
    }).enhanced).toEqual({ enabled: false, color: '#38bdf8' });
  });

  it('rejects invalid known values while allowing unknown future fields', () => {
    expect(isValidThumbnailStatusBordersDocument({ favorite: { enabled: 'yes' } })).toBe(false);
    expect(isValidThumbnailStatusBordersDocument({ enhanced: { color: 'yellow' } })).toBe(false);
    expect(isValidThumbnailStatusBordersDocument({ favorite: { color: 'rainbow' } })).toBe(false);
    expect(isValidThumbnailStatusBordersDocument({ enhanced: { color: 'rainbow' } })).toBe(true);
    expect(isValidThumbnailStatusBordersDocument({ futureStatus: { color: 'anything' } })).toBe(true);
  });
});
