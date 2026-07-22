import { describe, expect, it } from 'vitest';
import type { ImageFile } from './types';
import {
  buildDateSectionLayout,
  findDateSectionAnchorIndex,
  findDateSectionItemTop,
  formatSectionDate,
  shouldUseDateSectionLayout,
} from './dateSectionLayout';

function image(id: string, date: string): ImageFile {
  const createdAt = new Date(`${date}T12:00:00+09:00`).getTime();
  return {
    id,
    filename: `${id}.png`,
    absolutePath: id,
    fileUrl: `/api/image?path=${id}&thumb=true`,
    displayUrl: `/api/image?path=${id}&display=true`,
    fullUrl: `/api/image?path=${id}`,
    metadata: null,
    createdAt,
    mtime: createdAt,
  };
}

describe('date section layout', () => {
  it('creates separate headers for older loaded dates instead of hiding them behind placeholders', () => {
    const slots = [
      image('a', '2026-05-19'),
      image('b', '2026-05-18'),
      null,
      image('c', '2026-05-17'),
    ];

    const layout = buildDateSectionLayout({
      itemCount: slots.length,
      viewMode: 'grid',
      gridColumns: 2,
      gridCellWidth: 100,
      gridCellHeight: 100,
      getImageAt: (index) => slots[index],
    });

    expect(layout?.entries.filter((entry) => entry.type === 'header').map((entry) => entry.dateLabel)).toEqual([
      '5月19日',
      '5月18日',
      '5月17日',
    ]);
    expect(findDateSectionItemTop(layout, 3)).toBeGreaterThan(findDateSectionItemTop(layout, 1) ?? 0);
  });

  it('formats Japanese month/day labels without relying on file encoding artifacts', () => {
    expect(formatSectionDate(new Date(2026, 4, 19, 12, 0, 0).getTime())).toBe('5月19日');
  });
  it('keeps large incomplete result sets on the lightweight virtual layout while paging', () => {
    expect(shouldUseDateSectionLayout({
      showDateSeparators: true,
      itemCount: 50000,
      loadedSearchCount: 100,
      searchTotal: 50000,
      isClientFiltered: false,
    })).toBe(false);
  });

  it('allows full date sections for small fully loaded result sets', () => {
    expect(shouldUseDateSectionLayout({
      showDateSeparators: true,
      itemCount: 100,
      loadedSearchCount: 100,
      searchTotal: 100,
      isClientFiltered: false,
    })).toBe(true);
  });

  it('anchors a zoom to the item nearest the horizontal viewport center', () => {
    const slots = Array.from({ length: 10 }, (_, index) => image(`same-day-${index}`, '2026-05-19'));
    const layout = buildDateSectionLayout({
      itemCount: slots.length,
      viewMode: 'grid',
      gridColumns: 5,
      gridCellWidth: 100,
      gridCellHeight: 100,
      getImageAt: (index) => slots[index],
    });

    expect(findDateSectionAnchorIndex(layout, 88, 290)).toBe(2);
  });

  it('keeps large result sets on the lightweight virtual layout even after metadata pages load', () => {
    expect(shouldUseDateSectionLayout({
      showDateSeparators: true,
      itemCount: 50000,
      loadedSearchCount: 50000,
      searchTotal: 50000,
      isClientFiltered: false,
    })).toBe(false);
  });

  it('keeps client-filtered date sections off until the backing search is complete', () => {
    expect(shouldUseDateSectionLayout({
      showDateSeparators: true,
      itemCount: 10,
      loadedSearchCount: 100,
      searchTotal: 500,
      isClientFiltered: true,
    })).toBe(false);

    expect(shouldUseDateSectionLayout({
      showDateSeparators: true,
      itemCount: 10,
      loadedSearchCount: 500,
      searchTotal: 500,
      isClientFiltered: true,
    })).toBe(true);
  });
});
