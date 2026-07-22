import { describe, expect, it } from 'vitest';
import { appendDirSet, formatDirSet, parseDirSet, removeFromDirSet, summarizeDirSet } from './pathSet';

describe('pathSet helpers', () => {
  it('parses newline-separated folder sets and removes blank lines', () => {
    expect(parseDirSet('C:\\A\r\n\r\n C:\\B \n')).toEqual(['C:\\A', 'C:\\B']);
  });

  it('deduplicates paths case-insensitively while preserving first spelling', () => {
    expect(formatDirSet(['C:\\Images', 'c:\\images', 'D:\\More'])).toBe('C:\\Images\nD:\\More');
  });

  it('appends one or more folders without duplicating existing paths', () => {
    const result = appendDirSet('C:\\Images\nD:\\More', ['d:\\more', 'E:\\New']);
    expect(result).toBe('C:\\Images\nD:\\More\nE:\\New');
  });

  it('appends newline pasted folders', () => {
    const result = appendDirSet('C:\\Images', 'D:\\More\nE:\\New\n');
    expect(result).toBe('C:\\Images\nD:\\More\nE:\\New');
  });

  it('removes a folder case-insensitively', () => {
    const result = removeFromDirSet('C:\\Images\nD:\\More\nE:\\New', 'd:\\more');
    expect(result).toBe('C:\\Images\nE:\\New');
  });

  it('summarizes multi-folder sets compactly', () => {
    expect(summarizeDirSet('C:\\Images\nD:\\More')).toBe('2 folders: Images ...');
  });
});
