import React from 'react';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useImageStore } from '../store/ImageContext';
import SearchBar from './SearchBar';

vi.mock('../store/ImageContext', () => ({
  useImageStore: vi.fn(),
}));

const setSearchQuery = vi.fn();
let historyState: string[] = [];

function renderSearchBar(searchQuery = '') {
  vi.mocked(useImageStore).mockReturnValue({
    searchQuery,
    setSearchQuery,
  } as unknown as ReturnType<typeof useImageStore>);
  return render(<SearchBar />);
}

async function settleTagFetch() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

function dragHandle(chip: HTMLElement) {
  return within(chip).getByTestId('search-tag-drag-handle');
}

function mockChipLayout(chips: HTMLElement[], positions: Array<{ left: number; top: number }>) {
  chips.forEach((chip, index) => {
    const { left, top } = positions[index];
    vi.spyOn(chip, 'getBoundingClientRect').mockReturnValue({
      x: left,
      y: top,
      left,
      top,
      width: 80,
      height: 26,
      right: left + 80,
      bottom: top + 26,
      toJSON: () => ({}),
    } as DOMRect);
  });
}

function mockPointerCapture(handle: HTMLElement) {
  const captured = new Set<number>();
  const setPointerCapture = vi.fn((pointerId: number) => captured.add(pointerId));
  const hasPointerCapture = vi.fn((pointerId: number) => captured.has(pointerId));
  const releasePointerCapture = vi.fn((pointerId: number) => captured.delete(pointerId));
  Object.defineProperties(handle, {
    setPointerCapture: { configurable: true, value: setPointerCapture },
    hasPointerCapture: { configurable: true, value: hasPointerCapture },
    releasePointerCapture: { configurable: true, value: releasePointerCapture },
  });
  return { setPointerCapture, releasePointerCapture };
}

function mockDataTransfer() {
  const values = new Map<string, string>();
  return {
    effectAllowed: 'none',
    dropEffect: 'none',
    setData: vi.fn((type: string, value: string) => values.set(type, value)),
    getData: vi.fn((type: string) => values.get(type) ?? ''),
  } as unknown as DataTransfer;
}

beforeEach(() => {
  vi.clearAllMocks();
  historyState = ['cat, dog', 'landscape, night'];
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.startsWith('/api/tags')) {
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve({
          tags: [
            { tag: 'cat', count: 12 },
            { tag: 'castle', count: 7 },
            { tag: 'dog', count: 4 },
          ],
        }),
      } as Response);
    }

    if (url === '/api/search-history' && init?.method === 'PUT') {
      const query = String(JSON.parse(String(init.body)).query);
      historyState = [query, ...historyState.filter((entry) => entry.toLowerCase() !== query.toLowerCase())];
    } else if (url === '/api/search-history' && init?.method === 'DELETE') {
      const body = JSON.parse(String(init.body)) as { query?: string; clear?: boolean };
      historyState = body.clear
        ? []
        : historyState.filter((entry) => entry !== body.query);
    }
    return Promise.resolve({
      ok: true,
      json: () => Promise.resolve({ ok: true, entries: [...historyState] }),
    } as Response);
  }));
});

describe('SearchBar accessibility', () => {
  it('reloads shared history on focus and reopens it on click after Escape', async () => {
    const user = userEvent.setup();
    renderSearchBar();
    const input = screen.getByRole('combobox', { name: 'Search tags' });

    await user.click(input);
    const history = await screen.findByRole('region', { name: 'Search history' });
    expect(within(history).getByRole('button', { name: 'Use search cat, dog' })).toBeInTheDocument();
    expect(input).toHaveAttribute('aria-expanded', 'false');
    expect(input).not.toHaveAttribute('aria-activedescendant');

    fireEvent.keyDown(input, { key: 'Escape' });
    expect(screen.queryByRole('region', { name: 'Search history' })).not.toBeInTheDocument();
    await user.click(input);
    expect(await screen.findByRole('region', { name: 'Search history' })).toBeInTheDocument();

    const historyReads = vi.mocked(fetch).mock.calls.filter(([url, init]) => (
      String(url) === '/api/search-history' && !init?.method
    ));
    expect(historyReads.length).toBeGreaterThanOrEqual(2);
  });

  it('cycles history from the input and announces the active complete query', async () => {
    const user = userEvent.setup();
    renderSearchBar();
    const input = screen.getByRole('combobox', { name: 'Search tags' });
    await user.click(input);
    const history = await screen.findByRole('region', { name: 'Search history' });
    const first = within(history).getByRole('button', { name: 'Use search cat, dog' });
    const status = screen.getByRole('status', { name: 'Search history status' });

    fireEvent.keyDown(input, { key: 'ArrowDown' });
    expect(first).toHaveClass('selected');
    expect(status).toHaveTextContent('Search history 1 of 2: cat, dog');
    expect(input).toHaveFocus();
    expect(input).not.toHaveAttribute('aria-activedescendant');

    fireEvent.keyDown(input, { key: 'Escape' });
    expect(screen.queryByRole('region', { name: 'Search history' })).not.toBeInTheDocument();
    await user.click(input);
    const reopenedHistory = await screen.findByRole('region', { name: 'Search history' });
    const reopenedFirst = within(reopenedHistory).getByRole('button', { name: 'Use search cat, dog' });
    const reopenedLast = within(reopenedHistory).getByRole('button', { name: 'Use search landscape, night' });

    fireEvent.keyDown(input, { key: 'ArrowUp' });
    expect(reopenedLast).toHaveClass('selected');
    expect(status).toHaveTextContent('Search history 2 of 2: landscape, night');

    fireEvent.keyDown(input, { key: 'ArrowDown' });
    expect(reopenedFirst).toHaveClass('selected');
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(screen.queryByRole('region', { name: 'Search history' })).not.toBeInTheDocument();
    expect(input).toHaveFocus();
    expect(setSearchQuery).toHaveBeenCalledWith('cat, dog');
  });

  it('moves real focus between history rows and returns it to the input on Escape', async () => {
    const user = userEvent.setup();
    renderSearchBar();
    const input = screen.getByRole('combobox', { name: 'Search tags' });
    await user.click(input);
    const history = await screen.findByRole('region', { name: 'Search history' });
    const first = within(history).getByRole('button', { name: 'Use search cat, dog' });
    const last = within(history).getByRole('button', { name: 'Use search landscape, night' });

    act(() => first.focus());
    fireEvent.keyDown(first, { key: 'ArrowUp' });
    expect(last).toHaveFocus();
    expect(last).toHaveClass('selected');

    fireEvent.keyDown(last, { key: 'ArrowDown' });
    expect(first).toHaveFocus();
    fireEvent.keyDown(first, { key: 'Escape' });

    expect(screen.queryByRole('region', { name: 'Search history' })).not.toBeInTheDocument();
    expect(input).toHaveFocus();
    expect(setSearchQuery).not.toHaveBeenCalled();
  });

  it('applies a focused history row with Enter and restores input focus', async () => {
    const user = userEvent.setup();
    renderSearchBar();
    const input = screen.getByRole('combobox', { name: 'Search tags' });
    await user.click(input);
    const history = await screen.findByRole('region', { name: 'Search history' });
    const last = within(history).getByRole('button', { name: 'Use search landscape, night' });

    act(() => last.focus());
    fireEvent.keyDown(last, { key: 'Enter' });

    expect(screen.queryByRole('region', { name: 'Search history' })).not.toBeInTheDocument();
    expect(input).toHaveFocus();
    expect(setSearchQuery).toHaveBeenCalledWith('landscape, night');
  });

  it('does not select or recommit an invisible stale history row while reload is pending', async () => {
    const user = userEvent.setup();
    renderSearchBar();
    const input = screen.getByRole('combobox', { name: 'Search tags' });
    await user.click(input);
    await screen.findByRole('region', { name: 'Search history' });
    fireEvent.keyDown(input, { key: 'Escape' });

    const fetchMock = vi.mocked(fetch);
    const previousImplementation = fetchMock.getMockImplementation();
    if (!previousImplementation) throw new Error('Expected the shared fetch mock');
    let resolveReload!: (response: Response) => void;
    const pendingReload = new Promise<Response>((resolve) => {
      resolveReload = resolve;
    });
    fetchMock.mockImplementation((url, init) => {
      if (String(url) === '/api/search-history' && !init?.method) return pendingReload;
      return previousImplementation(url, init);
    });

    await user.click(input);
    expect(screen.getByRole('region', { name: 'Search history' })).toHaveTextContent('Loading search history...');
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(setSearchQuery).not.toHaveBeenCalled();
    const historyWritesWhilePending = fetchMock.mock.calls.filter(([url, init]) => (
      String(url) === '/api/search-history' && init?.method === 'PUT'
    ));
    expect(historyWritesWhilePending).toHaveLength(0);

    await act(async () => {
      resolveReload({
        ok: true,
        json: () => Promise.resolve({ ok: true, entries: ['fresh only'] }),
      } as Response);
      await pendingReload;
    });
    expect(await screen.findByRole('button', { name: 'Use search fresh only' })).toBeInTheDocument();
  });

  it('replaces the complete chips and draft query when a history row is selected', async () => {
    const user = userEvent.setup();
    renderSearchBar('portrait, warm');
    const input = screen.getByRole('combobox', { name: 'Search tags' });
    fireEvent.change(input, { target: { value: 'draft' } });
    fireEvent.change(input, { target: { value: '' } });
    await user.click(input);

    const history = await screen.findByRole('region', { name: 'Search history' });
    const historyAction = within(history).getByRole('button', { name: 'Use search cat, dog' });
    fireEvent.click(historyAction);

    expect(screen.queryByRole('listitem', { name: /Search tag portrait/i })).not.toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: /Search tag cat, position 1 of 2/i })).toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: /Search tag dog, position 2 of 2/i })).toBeInTheDocument();
    expect(input).toHaveValue('');
    expect(setSearchQuery).toHaveBeenCalledWith('cat, dog');
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([url, init]) => (
      String(url) === '/api/search-history'
      && init?.method === 'PUT'
      && JSON.parse(String(init.body)).query === 'cat, dog'
    ))).toBe(true));
  });

  it('deletes one row and clears all without selecting a history query', async () => {
    const user = userEvent.setup();
    renderSearchBar();
    const input = screen.getByRole('combobox', { name: 'Search tags' });
    await user.click(input);
    await screen.findByRole('region', { name: 'Search history' });

    await user.click(screen.getByRole('button', { name: 'Remove cat, dog from search history' }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Use search cat, dog' })).not.toBeInTheDocument());
    expect(setSearchQuery).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Clear all' }));
    expect(await screen.findByText('No recent searches')).toBeInTheDocument();
    expect(screen.queryByRole('option')).not.toBeInTheDocument();
    expect(setSearchQuery).not.toHaveBeenCalled();
  });

  it('does not persist draft debounce updates and saves only the effective query on final blur', async () => {
    renderSearchBar('dog');
    const input = screen.getByRole('combobox', { name: 'Search tags' });
    fireEvent.change(input, { target: { value: '  castle  ' } });

    await waitFor(() => expect(setSearchQuery).toHaveBeenCalledWith('dog, castle'));
    expect(vi.mocked(fetch).mock.calls.some(([url, init]) => (
      String(url) === '/api/search-history' && init?.method === 'PUT'
    ))).toBe(false);

    fireEvent.blur(input, { relatedTarget: null });
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([url, init]) => (
      String(url) === '/api/search-history'
      && init?.method === 'PUT'
      && JSON.parse(String(init.body)).query === 'dog, castle'
    ))).toBe(true));
  });

  it('keeps tag suggestions additive and records the resulting complete query', async () => {
    renderSearchBar('dog');
    const input = screen.getByRole('combobox', { name: 'Search tags' });
    fireEvent.change(input, { target: { value: 'ca' } });
    const suggestions = await screen.findByRole('listbox', { name: 'Tag suggestions' });
    fireEvent.mouseDown(within(suggestions).getByRole('option', { name: /cat/i }));

    expect(setSearchQuery).not.toHaveBeenCalledWith('cat');
    await waitFor(() => expect(setSearchQuery).toHaveBeenCalledWith('dog, cat'));
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([url, init]) => (
      String(url) === '/api/search-history'
      && init?.method === 'PUT'
      && JSON.parse(String(init.body)).query === 'dog, cat'
    ))).toBe(true));
  });

  it('exposes suggestions through a combobox and selects the active option with keyboard', async () => {
    renderSearchBar();

    const input = screen.getByRole('combobox', { name: 'Search tags' });
    fireEvent.change(input, { target: { value: 'ca' } });

    const listbox = await screen.findByRole('listbox', { name: 'Tag suggestions' });
    const options = screen.getAllByRole('option');
    expect(listbox).toContainElement(options[0]);
    expect(input).toHaveAttribute('aria-expanded', 'true');
    expect(input).toHaveAttribute('aria-controls', listbox.id);

    fireEvent.keyDown(input, { key: 'ArrowDown' });
    expect(options[0]).toHaveAttribute('aria-selected', 'true');
    expect(input).toHaveAttribute('aria-activedescendant', options[0].id);

    fireEvent.keyDown(input, { key: 'Enter' });
    expect(screen.getByText('cat')).toBeInTheDocument();
    expect(input).toHaveFocus();
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });

  it('closes the listbox with Escape and announces an empty suggestion result', async () => {
    renderSearchBar();

    const input = screen.getByRole('combobox', { name: 'Search tags' });
    fireEvent.change(input, { target: { value: 'ca' } });
    await screen.findByRole('listbox', { name: 'Tag suggestions' });
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Escape' });

    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(input).toHaveAttribute('aria-expanded', 'false');
    expect(input).not.toHaveAttribute('aria-activedescendant');

    fireEvent.change(input, { target: { value: 'unmatched' } });
    await waitFor(() => {
      expect(screen.getByRole('status', { name: 'Tag suggestion status' })).toHaveTextContent('No tag suggestions available.');
    });
    expect(input).toHaveAttribute('aria-expanded', 'false');
  });

  it('keeps clear and committed-tag removal controls labeled after icon rendering', async () => {
    renderSearchBar('cat');
    await settleTagFetch();

    expect(screen.getByRole('button', { name: 'Clear all search tags' })).toBeInTheDocument();
    const removeTag = screen.getByRole('button', { name: 'Remove tag cat' });
    expect(removeTag).toHaveAttribute('title', 'Remove cat');

    fireEvent.click(removeTag);
    expect(screen.queryByRole('button', { name: 'Remove tag cat' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Clear all search tags' })).not.toBeInTheDocument();
  });

  it('reorders focused tags with Alt Shift arrows and keeps focus on the moved tag', async () => {
    renderSearchBar('cat, dog, castle');

    const catChip = screen.getByRole('listitem', { name: /Search tag cat, position 1 of 3/i });
    catChip.focus();
    fireEvent.keyDown(catChip, { key: 'ArrowRight', altKey: true, shiftKey: true });

    const movedCat = screen.getByRole('listitem', { name: /Search tag cat, position 2 of 3/i });
    expect(movedCat).toHaveFocus();
    expect(movedCat).toHaveAttribute('aria-posinset', '2');
    expect(movedCat).toHaveAttribute('aria-setsize', '3');
    expect(screen.getByRole('status', { name: 'Search tag arrangement' }))
      .toHaveTextContent('Moved tag cat to position 2 of 3.');
    await waitFor(() => expect(setSearchQuery).toHaveBeenCalledWith('dog, cat, castle'));

    fireEvent.keyDown(movedCat, { key: 'ArrowLeft', altKey: true, shiftKey: true });
    const restoredCat = screen.getByRole('listitem', { name: /Search tag cat, position 1 of 3/i });
    expect(restoredCat).toHaveFocus();
    expect(restoredCat).toHaveAttribute('aria-posinset', '1');
    expect(screen.getByRole('status', { name: 'Search tag arrangement' }))
      .toHaveTextContent('Moved tag cat to position 1 of 3.');
  });

  it('keeps a sub-threshold touch gesture as a focused tap without reordering or removing', async () => {
    renderSearchBar('cat, dog');
    await settleTagFetch();
    const chips = screen.getAllByRole('listitem');
    mockChipLayout(chips, [{ left: 0, top: 0 }, { left: 100, top: 0 }]);
    const handle = dragHandle(chips[0]);
    const capture = mockPointerCapture(handle);

    fireEvent.pointerDown(handle, {
      pointerId: 41,
      pointerType: 'touch',
      isPrimary: true,
      button: 0,
      clientX: 10,
      clientY: 13,
    });
    fireEvent.pointerMove(handle, {
      pointerId: 41,
      pointerType: 'touch',
      clientX: 17,
      clientY: 13,
    });
    fireEvent.pointerUp(handle, {
      pointerId: 41,
      pointerType: 'touch',
      clientX: 17,
      clientY: 13,
    });

    expect(screen.getByRole('listitem', { name: /Search tag cat, position 1 of 2/i })).toHaveFocus();
    expect(screen.getByRole('listitem', { name: /Search tag dog, position 2 of 2/i })).toBeInTheDocument();
    expect(screen.getByRole('status', { name: 'Search tag arrangement' })).toBeEmptyDOMElement();
    expect(screen.getByRole('button', { name: 'Remove tag cat' })).toBeInTheDocument();
    expect(capture.setPointerCapture).toHaveBeenCalledWith(41);
    expect(capture.releasePointerCapture).toHaveBeenCalledWith(41);
  });

  it('reorders after and before across horizontal and wrapped vertical chip layouts', async () => {
    const view = renderSearchBar('cat, dog, castle');
    let chips = screen.getAllByRole('listitem');
    mockChipLayout(chips, [
      { left: 0, top: 0 },
      { left: 100, top: 0 },
      { left: 200, top: 0 },
    ]);
    let handle = dragHandle(chips[0]);
    mockPointerCapture(handle);

    fireEvent.pointerDown(handle, {
      pointerId: 42,
      pointerType: 'touch',
      isPrimary: true,
      button: 0,
      clientX: 10,
      clientY: 13,
    });
    fireEvent.pointerMove(handle, {
      pointerId: 42,
      pointerType: 'touch',
      clientX: 240,
      clientY: 13,
    });
    expect(chips[0]).toHaveClass('is-dragging');
    fireEvent.pointerUp(handle, {
      pointerId: 42,
      pointerType: 'touch',
      clientX: 240,
      clientY: 13,
    });

    let movedCat = screen.getByRole('listitem', { name: /Search tag cat, position 3 of 3/i });
    expect(movedCat).toHaveFocus();
    expect(screen.getByRole('status', { name: 'Search tag arrangement' }))
      .toHaveTextContent('Moved tag cat to position 3 of 3.');
    await waitFor(() => expect(setSearchQuery).toHaveBeenCalledWith('dog, castle, cat'));
    vi.mocked(useImageStore).mockReturnValue({
      searchQuery: 'dog, castle, cat',
      setSearchQuery,
    } as unknown as ReturnType<typeof useImageStore>);
    view.rerender(<SearchBar />);

    chips = screen.getAllByRole('listitem');
    mockChipLayout(chips, [
      { left: 0, top: 0 },
      { left: 0, top: 40 },
      { left: 0, top: 80 },
    ]);
    movedCat = screen.getByRole('listitem', { name: /Search tag cat, position 3 of 3/i });
    handle = dragHandle(movedCat);
    mockPointerCapture(handle);
    fireEvent.pointerDown(handle, {
      pointerId: 43,
      pointerType: 'touch',
      isPrimary: true,
      button: 0,
      clientX: 40,
      clientY: 93,
    });
    fireEvent.pointerMove(handle, {
      pointerId: 43,
      pointerType: 'touch',
      clientX: 40,
      clientY: 13,
    });
    fireEvent.pointerUp(handle, {
      pointerId: 43,
      pointerType: 'touch',
      clientX: 40,
      clientY: 13,
    });

    const restoredCat = screen.getByRole('listitem', { name: /Search tag cat, position 1 of 3/i });
    expect(restoredCat).toHaveFocus();
    expect(screen.getByRole('status', { name: 'Search tag arrangement' }))
      .toHaveTextContent('Moved tag cat to position 1 of 3.');
    await waitFor(() => expect(setSearchQuery).toHaveBeenCalledWith('cat, dog, castle'));
  });

  it('cancels an active captured pointer drag without changing the query', async () => {
    renderSearchBar('cat, dog, castle');
    await settleTagFetch();
    const chips = screen.getAllByRole('listitem');
    mockChipLayout(chips, [
      { left: 0, top: 0 },
      { left: 100, top: 0 },
      { left: 200, top: 0 },
    ]);
    const handle = dragHandle(chips[0]);
    const capture = mockPointerCapture(handle);

    fireEvent.pointerDown(handle, {
      pointerId: 44,
      pointerType: 'pen',
      isPrimary: true,
      button: 0,
      clientX: 10,
      clientY: 13,
    });
    fireEvent.pointerMove(handle, {
      pointerId: 44,
      pointerType: 'pen',
      clientX: 140,
      clientY: 13,
    });
    fireEvent.pointerCancel(handle, { pointerId: 44, pointerType: 'pen' });

    expect(screen.getByRole('listitem', { name: /Search tag cat, position 1 of 3/i })).toHaveFocus();
    expect(screen.getByRole('listitem', { name: /Search tag dog, position 2 of 3/i })).toBeInTheDocument();
    expect(screen.getByRole('status', { name: 'Search tag arrangement' }))
      .toHaveTextContent('Reordering tag cat canceled.');
    expect(chips[0]).not.toHaveClass('is-dragging');
    expect(capture.releasePointerCapture).toHaveBeenCalledWith(44);
    expect(setSearchQuery).not.toHaveBeenCalled();
  });

  it('keeps desktop HTML5 drag on the same focus and live-region reorder contract', async () => {
    renderSearchBar('cat, dog, castle');
    const chips = screen.getAllByRole('listitem');
    const dataTransfer = mockDataTransfer();

    fireEvent.dragStart(chips[0], { dataTransfer });
    fireEvent.dragOver(chips[1], { dataTransfer });
    fireEvent.drop(chips[1], { dataTransfer });

    const movedCat = screen.getByRole('listitem', { name: /Search tag cat, position 2 of 3/i });
    expect(movedCat).toHaveFocus();
    expect(screen.getByRole('status', { name: 'Search tag arrangement' }))
      .toHaveTextContent('Moved tag cat to position 2 of 3.');
    await waitFor(() => expect(setSearchQuery).toHaveBeenCalledWith('dog, cat, castle'));
  });

  it('moves the selected duplicate tag and rejects a stale pointer index after external query replacement', async () => {
    const view = renderSearchBar('cat, cat, dog');
    let chips = screen.getAllByRole('listitem');
    mockChipLayout(chips, [
      { left: 0, top: 0 },
      { left: 100, top: 0 },
      { left: 200, top: 0 },
    ]);
    let secondCat = screen.getByRole('listitem', { name: /Search tag cat, position 2 of 3/i });
    let handle = dragHandle(secondCat);
    mockPointerCapture(handle);
    fireEvent.pointerDown(handle, {
      pointerId: 45,
      pointerType: 'touch',
      isPrimary: true,
      button: 0,
      clientX: 140,
      clientY: 13,
    });
    fireEvent.pointerMove(handle, {
      pointerId: 45,
      pointerType: 'touch',
      clientX: 240,
      clientY: 13,
    });
    fireEvent.pointerUp(handle, {
      pointerId: 45,
      pointerType: 'touch',
      clientX: 240,
      clientY: 13,
    });

    expect(screen.getByRole('listitem', { name: /Search tag cat, position 3 of 3/i })).toHaveFocus();
    await waitFor(() => expect(setSearchQuery).toHaveBeenCalledWith('cat, dog, cat'));

    chips = screen.getAllByRole('listitem');
    mockChipLayout(chips, [
      { left: 0, top: 0 },
      { left: 100, top: 0 },
      { left: 200, top: 0 },
    ]);
    const dog = screen.getByRole('listitem', { name: /Search tag dog, position 2 of 3/i });
    handle = dragHandle(dog);
    mockPointerCapture(handle);
    fireEvent.pointerDown(handle, {
      pointerId: 46,
      pointerType: 'touch',
      isPrimary: true,
      button: 0,
      clientX: 140,
      clientY: 13,
    });
    fireEvent.pointerMove(handle, {
      pointerId: 46,
      pointerType: 'touch',
      clientX: 240,
      clientY: 13,
    });

    vi.mocked(useImageStore).mockReturnValue({
      searchQuery: 'cat, castle',
      setSearchQuery,
    } as unknown as ReturnType<typeof useImageStore>);
    view.rerender(<SearchBar />);
    await waitFor(() => {
      expect(screen.queryByRole('listitem', { name: /Search tag dog/i })).not.toBeInTheDocument();
    });
    const currentHandle = dragHandle(screen.getByRole('listitem', { name: /Search tag cat, position 1 of 2/i }));
    fireEvent.pointerUp(currentHandle, {
      pointerId: 46,
      pointerType: 'touch',
      clientX: 240,
      clientY: 13,
    });

    expect(screen.getByRole('listitem', { name: /Search tag cat, position 1 of 2/i })).toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: /Search tag castle, position 2 of 2/i })).toBeInTheDocument();
    expect(screen.getByRole('status', { name: 'Search tag arrangement' }))
      .toHaveTextContent('canceled because the tag list changed.');
  });

  it('removes only the focused tag with Delete and moves focus to the next chip', async () => {
    renderSearchBar('cat, dog, castle');

    const dogChip = screen.getByRole('listitem', { name: /Search tag dog, position 2 of 3/i });
    dogChip.focus();
    fireEvent.keyDown(dogChip, { key: 'Delete' });

    expect(screen.queryByRole('listitem', { name: /Search tag dog/i })).not.toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: /Search tag castle, position 2 of 2/i })).toHaveFocus();
    expect(screen.getByRole('listitem', { name: /Search tag cat, position 1 of 2/i })).toBeInTheDocument();
    expect(screen.getByRole('status', { name: 'Search tag arrangement' })).toHaveTextContent('Removed tag dog.');
    await waitFor(() => expect(setSearchQuery).toHaveBeenCalledWith('cat, castle'));
  });

  it('removes the final focused tag with Backspace and returns focus to the input', async () => {
    renderSearchBar('cat');

    const catChip = screen.getByRole('listitem', { name: /Search tag cat, position 1 of 1/i });
    catChip.focus();
    fireEvent.keyDown(catChip, { key: 'Backspace' });

    expect(screen.queryByRole('listitem')).not.toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Search tags' })).toHaveFocus();
    expect(screen.getByRole('status', { name: 'Search tag arrangement' })).toHaveTextContent('Removed tag cat.');
    await waitFor(() => expect(setSearchQuery).toHaveBeenCalledWith(''));
  });

  it('keeps duplicate external query tags independently addressable', async () => {
    renderSearchBar('cat, cat, dog');

    const secondCat = screen.getByRole('listitem', { name: /Search tag cat, position 2 of 3/i });
    secondCat.focus();
    fireEvent.keyDown(secondCat, { key: 'ArrowRight', altKey: true, shiftKey: true });

    const movedDuplicate = screen.getByRole('listitem', { name: /Search tag cat, position 3 of 3/i });
    expect(movedDuplicate).toHaveFocus();
    await waitFor(() => expect(setSearchQuery).toHaveBeenCalledWith('cat, dog, cat'));

    fireEvent.keyDown(movedDuplicate, { key: 'Delete' });

    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getAllByText('cat')).toHaveLength(1);
    expect(screen.getByRole('listitem', { name: /Search tag cat, position 1 of 2/i })).toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: /Search tag dog, position 2 of 2/i })).toHaveFocus();
  });

  it('lets the remove button own Enter and Space without triggering chip keyboard actions', async () => {
    const user = userEvent.setup();
    renderSearchBar('cat, dog, castle');

    const removeCat = screen.getByRole('button', { name: 'Remove tag cat' });
    removeCat.focus();
    await user.keyboard('{Enter}');

    expect(screen.queryByRole('listitem', { name: /Search tag cat/i })).not.toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: /Search tag dog, position 1 of 2/i })).toBeInTheDocument();
    expect(screen.getByRole('status', { name: 'Search tag arrangement' })).toHaveTextContent('Removed tag cat.');

    const removeDog = screen.getByRole('button', { name: 'Remove tag dog' });
    removeDog.focus();
    await user.keyboard(' ');

    expect(screen.queryByRole('listitem', { name: /Search tag dog/i })).not.toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: /Search tag castle, position 1 of 1/i })).toBeInTheDocument();
    expect(screen.getByRole('status', { name: 'Search tag arrangement' })).toHaveTextContent('Removed tag dog.');
  });
});
