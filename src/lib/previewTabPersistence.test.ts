import { describe, expect, it } from 'vitest';
import {
  MAX_PERSISTED_PREVIEW_TABS,
  normalizePersistedPreviewTabs,
} from './previewTabPersistence';

describe('preview tab persistence', () => {
  it('keeps a unique, ordered, capped set of supported absolute image paths', () => {
    const ids = Array.from({ length: MAX_PERSISTED_PREVIEW_TABS + 2 }, (_, index) =>
      `C:/images/${index}.png`
    );

    expect(normalizePersistedPreviewTabs({
      version: 1,
      tabIds: [ids[0], ids[0], ...ids.slice(1)],
      activeId: ids[1],
    })).toEqual({
      version: 1,
      tabIds: ids.slice(0, MAX_PERSISTED_PREVIEW_TABS),
      activeId: ids[1],
    });
  });

  it.each([
    null,
    { version: 0, tabIds: ['C:/images/a.png'], activeId: 'C:/images/a.png' },
    { version: 1, tabIds: ['relative.png'], activeId: 'relative.png' },
    { version: 1, tabIds: ['C:/images/a.gif'], activeId: 'C:/images/a.gif' },
    { version: 1, tabIds: 'C:/images/a.png', activeId: 'C:/images/a.png' },
  ])('normalizes malformed or obsolete snapshots to an empty state', (value) => {
    expect(normalizePersistedPreviewTabs(value)).toEqual({
      version: 1,
      tabIds: [],
      activeId: null,
    });
  });

  it('falls back to the first restored tab when the active id is absent', () => {
    expect(normalizePersistedPreviewTabs({
      version: 1,
      tabIds: ['C:/images/a.webp', 'C:/images/b.jpeg'],
      activeId: 'C:/images/missing.png',
    })).toEqual({
      version: 1,
      tabIds: ['C:/images/a.webp', 'C:/images/b.jpeg'],
      activeId: 'C:/images/a.webp',
    });
  });
});
