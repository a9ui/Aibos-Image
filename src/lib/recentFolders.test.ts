import { describe, expect, it } from 'vitest';

import {
  buildSharedRecentFolders,
  mergeRecentFolderMemories,
  normalizeRecentFolderMemory,
  normalizeSharedRecentFolders,
  rememberRecentFolderSet,
  sharedRecentToLocalMemory,
} from './recentFolders';

describe('shared recent folders', () => {
  it('normalizes browser-local single and multi-folder sets case-insensitively', () => {
    const memory = normalizeRecentFolderMemory({
      recentDirs: [
        'C:/Newest\nD:/Extra\nC:/NEWEST',
        'c:/newest\nd:/extra',
        'E:/Second',
        'F:/Third',
        'G:/Fourth',
        'H:/Fifth',
        'I:/Sixth',
        'J:/Seventh',
        'K:/Eighth',
        'L:/Over limit',
      ],
      lastDirSet: 'M:/Last\nm:/last',
    });

    expect(memory).toEqual({
      recentDirs: [
        'C:/Newest\nD:/Extra',
        'E:/Second',
        'F:/Third',
        'G:/Fourth',
        'H:/Fifth',
        'I:/Sixth',
        'J:/Seventh',
        'K:/Eighth',
        'L:/Over limit',
      ],
      lastDirSet: 'M:/Last',
    });
  });

  it('remembers the latest spelling first without leaving a case-only duplicate', () => {
    expect(rememberRecentFolderSet({
      recentDirs: ['C:/Images\nD:/Extra', 'E:/Old'],
      lastDirSet: 'E:/Old',
    }, 'c:/images\nd:/extra')).toEqual({
      recentDirs: ['c:/images\nd:/extra', 'E:/Old'],
      lastDirSet: 'c:/images\nd:/extra',
    });
  });

  it('merges additive memory in caller recency order with case-insensitive identity', () => {
    expect(mergeRecentFolderMemories({
      recentDirs: ['C:/Current', 'D:/Keep'],
      lastDirSet: 'C:/Current',
    }, {
      recentDirs: ['c:/current', 'E:/Legacy'],
      lastDirSet: 'E:/Legacy',
    })).toEqual({
      recentDirs: ['C:/Current', 'D:/Keep', 'E:/Legacy'],
      lastDirSet: 'C:/Current',
    });
  });

  it('normalizes shared recent folder sets into positive unique folder sets', () => {
    const shared = normalizeSharedRecentFolders({
      version: 1,
      lastFolderSet: ['C:/Images', 'C:/Images'],
      recentFolderSets: [
        ['C:/Images'],
        ['C:/Other', ''],
        ['c:/images'],
        [],
        ['C:/Third'],
      ],
      updatedAtUtc: '2026-07-09T00:00:00Z',
    });

    expect(shared).toMatchObject({
      version: 1,
      lastFolderSet: ['C:/Images'],
      recentFolderSets: [['C:/Images'], ['C:/Other'], ['C:/Third']],
      updatedAtUtc: '2026-07-09T00:00:00Z',
    });
  });

  it('builds shared JSON from browser local folder memory', () => {
    const shared = buildSharedRecentFolders({
      lastDirSet: 'C:/Selected\nC:/Extra',
      recentDirs: ['C:/Old', 'C:/Selected\nC:/Extra', 'C:/Another'],
    }, '2026-07-09T01:02:03Z');

    expect(shared).toEqual({
      version: 1,
      lastFolderSet: ['C:/Selected', 'C:/Extra'],
      recentFolderSets: [
        ['C:/Selected', 'C:/Extra'],
        ['C:/Old'],
        ['C:/Another'],
      ],
      updatedAtUtc: '2026-07-09T01:02:03Z',
    });
  });

  it('maps shared JSON back to browser localStorage shapes', () => {
    const memory = sharedRecentToLocalMemory({
      version: 1,
      lastFolderSet: ['C:/Selected', 'C:/Extra'],
      recentFolderSets: [['C:/Selected', 'C:/Extra'], ['C:/Old']],
      updatedAtUtc: '2026-07-09T01:02:03Z',
    });

    expect(memory).toEqual({
      lastDirSet: 'C:/Selected\nC:/Extra',
      recentDirs: ['C:/Selected\nC:/Extra', 'C:/Old'],
    });
  });
});
