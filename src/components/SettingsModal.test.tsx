import React from 'react';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import SettingsModal from './SettingsModal';
import { useImageStore } from '../store/ImageContext';
import { UNBOUND_KEY_BINDING } from '../lib/keyBindings';
import {
  DEFAULT_KEY_BINDINGS,
  DEFAULT_THUMBNAIL_STATUS_BORDERS,
  type ThumbnailStatusBorderSettings,
} from '../lib/types';

vi.mock('../store/ImageContext', () => ({
  useImageStore: vi.fn(),
}));

const setView = vi.fn();
const setKeyBindings = vi.fn();
const setShowSettings = vi.fn();
const setConfirmBeforeDelete = vi.fn();
const setThumbnailStatusBorders = vi.fn();
const writeText = vi.fn();

const fullRevision = '1234567890abcdef1234567890abcdef12345678';
const validRuntimePayload = {
  product: 'Aibos Image',
  sourceRevision: fullRevision,
  sourceDirty: false,
  buildId: 'build_2026-07-18',
  buildCompletedAtUtc: '2026-07-18T01:02:03.000Z',
  serverHost: '127.0.0.1',
  serverPort: 3011,
  serverStartedAtUtc: '2026-07-18T01:03:00.000Z',
  processId: 4321,
};

function runtimeResponse(payload: unknown, ok = true) {
  return { ok, json: async () => payload } as Response;
}

function mockSettingsStore(
  showSettings = true,
  confirmBeforeDelete = true,
  keyBindings = DEFAULT_KEY_BINDINGS,
  thumbnailStatusBorders: ThumbnailStatusBorderSettings = DEFAULT_THUMBNAIL_STATUS_BORDERS,
) {
  setKeyBindings.mockResolvedValue({ ok: true });
  setConfirmBeforeDelete.mockResolvedValue({ ok: true });
  setThumbnailStatusBorders.mockResolvedValue({ ok: true });
  vi.mocked(useImageStore).mockReturnValue({
    showSettings,
    setShowSettings,
    keyBindings,
    setKeyBindings,
    confirmBeforeDelete,
    setConfirmBeforeDelete,
    thumbnailStatusBorders,
    setThumbnailStatusBorders,
    view: {
      modalEdgeRatio: 0.28,
      showUnseenMarkers: true,
    },
    setView,
  } as unknown as ReturnType<typeof useImageStore>);
}

describe('SettingsModal unseen markers setting', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockSettingsStore();
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})));
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
  });

  it('closes through the shared dialog path when the backdrop is clicked', async () => {
    const user = userEvent.setup();
    render(<SettingsModal />);
    const backdrop = screen.getByTestId('settings-backdrop');

    await user.click(backdrop);

    expect(setShowSettings).toHaveBeenCalledWith(false);
  });

  it('reflects the current value and updates it through the checkbox', async () => {
    const user = userEvent.setup();
    render(<SettingsModal />);

    const checkbox = screen.getByRole('checkbox', { name: 'Show unseen markers' });
    expect(checkbox).toBeChecked();

    await user.click(checkbox);
    expect(setView).toHaveBeenCalledWith({ showUnseenMarkers: false });
  });

  it('shows solid favorite and enhanced defaults, then saves independent toggle/color settings', async () => {
    const user = userEvent.setup();
    render(<SettingsModal />);

    const favoriteToggle = screen.getByRole('checkbox', { name: 'Show favorite thumbnail border' });
    const enhancedToggle = screen.getByRole('checkbox', { name: 'Show enhanced thumbnail border' });
    const favoriteColor = screen.getByLabelText('Favorite thumbnail border color');
    const enhancedColor = screen.getByLabelText('AI enhanced thumbnail border color');
    expect(favoriteToggle).toBeChecked();
    expect(enhancedToggle).toBeChecked();
    expect(favoriteColor).toHaveValue('#facc15');
    expect(screen.queryByRole('combobox', { name: 'AI enhanced thumbnail border style' })).not.toBeInTheDocument();
    expect(enhancedColor).toHaveValue('#38bdf8');
    expect(enhancedColor).toBeEnabled();

    await user.click(favoriteToggle);
    fireEvent.change(enhancedColor, { target: { value: '#12abef' } });
    await user.click(screen.getByRole('button', { name: 'Save thumbnail borders' }));

    await waitFor(() => expect(setThumbnailStatusBorders).toHaveBeenCalledWith({
      favorite: { enabled: false, color: '#facc15' },
      enhanced: { enabled: true, color: '#12abef' },
    }));
  });

  it('saves only the thumbnail border preference changed in this settings session', async () => {
    const user = userEvent.setup();
    render(<SettingsModal />);

    await user.click(screen.getByRole('checkbox', { name: 'Show favorite thumbnail border' }));
    await user.click(screen.getByRole('button', { name: 'Save thumbnail borders' }));

    await waitFor(() => expect(setThumbnailStatusBorders).toHaveBeenCalledWith({
      favorite: { enabled: false, color: '#facc15' },
    }));
  });

  it('keeps a custom enhanced color directly editable without a rainbow style mode', async () => {
    mockSettingsStore(true, true, DEFAULT_KEY_BINDINGS, {
      favorite: { enabled: true, color: '#facc15' },
      enhanced: { enabled: true, color: '#12abef' },
    });
    const user = userEvent.setup();
    render(<SettingsModal />);

    const enhancedColor = screen.getByLabelText('AI enhanced thumbnail border color');
    expect(enhancedColor).toHaveValue('#12abef');

    fireEvent.change(enhancedColor, { target: { value: '#abcdef' } });

    expect(enhancedColor).toHaveValue('#abcdef');
    await user.click(screen.getByRole('button', { name: 'Save thumbnail borders' }));
    await waitFor(() => expect(setThumbnailStatusBorders).toHaveBeenCalledWith({
      enhanced: { enabled: true, color: '#abcdef' },
    }));
  });

  it('resets both controls to enabled favorite yellow and enhanced cyan, then saves those defaults', async () => {
    const user = userEvent.setup();
    render(<SettingsModal />);

    await user.click(screen.getByRole('checkbox', { name: 'Show enhanced thumbnail border' }));
    fireEvent.change(screen.getByLabelText('Favorite thumbnail border color'), {
      target: { value: '#000000' },
    });
    await user.click(screen.getByRole('button', { name: 'Reset border defaults' }));

    expect(screen.getByRole('checkbox', { name: 'Show favorite thumbnail border' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Show enhanced thumbnail border' })).toBeChecked();
    expect(screen.getByLabelText('Favorite thumbnail border color')).toHaveValue('#facc15');
    expect(screen.getByLabelText('AI enhanced thumbnail border color')).toHaveValue('#38bdf8');
    expect(screen.getByLabelText('AI enhanced thumbnail border color')).toBeEnabled();

    await user.click(screen.getByRole('button', { name: 'Save thumbnail borders' }));
    await waitFor(() => expect(setThumbnailStatusBorders).toHaveBeenCalledWith({
      favorite: { enabled: true, color: '#facc15' },
      enhanced: { enabled: true, color: '#38bdf8' },
    }));
  });

  it('shows conflicting fields inline and never saves the invalid draft on close', async () => {
    const user = userEvent.setup();
    render(<SettingsModal />);

    await user.click(screen.getByRole('button', { name: 'Next image binding' }));
    await user.keyboard('f');

    const nextBinding = screen.getByRole('button', { name: 'Next image binding' });
    const favoriteBinding = screen.getByRole('button', { name: 'Toggle favorite binding' });
    expect(nextBinding).toHaveAttribute('aria-invalid', 'true');
    expect(favoriteBinding).toHaveAttribute('aria-invalid', 'true');
    expect(nextBinding).toHaveAttribute('aria-describedby', 'key-binding-error-nextImage');
    expect(screen.getByText('Also assigned to Toggle favorite.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save key bindings' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Close settings' }));
    expect(setKeyBindings).not.toHaveBeenCalled();
    expect(setShowSettings).toHaveBeenCalledWith(false);
  });

  it('includes the modal filmstrip key in conflict detection', async () => {
    const user = userEvent.setup();
    render(<SettingsModal />);

    await user.click(screen.getByRole('button', { name: 'Next image binding' }));
    await user.keyboard('t');

    expect(screen.getByRole('button', { name: 'Next image binding' }))
      .toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('button', { name: 'Toggle modal filmstrip binding' }))
      .toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByText('Also assigned to Toggle modal filmstrip.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save key bindings' })).toBeDisabled();
  });

  it('shows an exhausted migration as unassigned and lets Settings repair it', async () => {
    mockSettingsStore(true, true, {
      ...DEFAULT_KEY_BINDINGS,
      addToAlbum: UNBOUND_KEY_BINDING,
    });
    const user = userEvent.setup();
    render(<SettingsModal />);

    const binding = screen.getByRole('button', { name: 'Add selection to Album binding' });
    expect(binding).toHaveTextContent('Unassigned');
    expect(binding).toHaveAttribute('aria-describedby', 'key-binding-error-addToAlbum');
    expect(screen.getByText('No collision-free migration key was available. Choose a key to enable this action.')).toBeVisible();

    await user.click(binding);
    await user.keyboard('p');
    expect(binding).toHaveTextContent('P');
    expect(screen.queryByText('No collision-free migration key was available. Choose a key to enable this action.')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Save key bindings' }));
    expect(setKeyBindings).toHaveBeenCalledWith({
      ...DEFAULT_KEY_BINDINGS,
      addToAlbum: 'p',
    });
  });

  it('resets bindings only through the explicit action, then saves a conflict-free draft', async () => {
    const user = userEvent.setup();
    render(<SettingsModal />);

    await user.click(screen.getByRole('button', { name: 'Next image binding' }));
    await user.keyboard('f');
    await user.click(screen.getByRole('button', { name: 'Reset to defaults' }));

    expect(screen.queryByText('Also assigned to Toggle favorite.')).not.toBeInTheDocument();
    const save = screen.getByRole('button', { name: 'Save key bindings' });
    expect(save).toBeEnabled();
    await user.click(save);
    expect(setKeyBindings).toHaveBeenCalledWith(DEFAULT_KEY_BINDINGS);
    await waitFor(() => expect(setShowSettings).toHaveBeenCalledWith(false));
  });

  it('keeps a rejected key-binding draft open and retries the same draft', async () => {
    setKeyBindings
      .mockResolvedValueOnce({ ok: false, error: 'Shared settings are temporarily unavailable. Try again.' })
      .mockResolvedValueOnce({ ok: true });
    const user = userEvent.setup();
    render(<SettingsModal />);

    await user.click(screen.getByRole('button', { name: 'Next image binding' }));
    await user.keyboard('n');
    await user.click(screen.getByRole('button', { name: 'Save key bindings' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Draft preserved');
    expect(screen.getByRole('dialog', { name: 'Settings' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Next image binding' })).toHaveTextContent('N');
    expect(screen.getByRole('button', { name: 'Retry save key bindings' })).toBeEnabled();
    expect(setShowSettings).not.toHaveBeenCalledWith(false);

    await user.click(screen.getByRole('button', { name: 'Retry save key bindings' }));
    await waitFor(() => expect(setShowSettings).toHaveBeenCalledWith(false));
    expect(setKeyBindings).toHaveBeenCalledTimes(2);
    expect(setKeyBindings).toHaveBeenNthCalledWith(1, {
      ...DEFAULT_KEY_BINDINGS,
      nextImage: 'n',
    });
    expect(setKeyBindings).toHaveBeenNthCalledWith(2, {
      ...DEFAULT_KEY_BINDINGS,
      nextImage: 'n',
    });
  });

  it('rolls back a rejected delete-confirmation toggle and can retry the requested value', async () => {
    setConfirmBeforeDelete
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce({ ok: true });
    const user = userEvent.setup();
    render(<SettingsModal />);

    const checkbox = screen.getByRole('checkbox', { name: 'Confirm before delete' });
    expect(checkbox).toBeChecked();
    await user.click(checkbox);

    expect(await screen.findByRole('alert')).toHaveTextContent('saved value was restored');
    expect(checkbox).toBeChecked();
    expect(screen.getByRole('button', { name: 'Retry delete confirmation change' })).toBeEnabled();

    await user.click(screen.getByRole('button', { name: 'Retry delete confirmation change' }));
    await waitFor(() => expect(checkbox).not.toBeChecked());
    expect(setConfirmBeforeDelete).toHaveBeenCalledTimes(2);
    expect(setConfirmBeforeDelete).toHaveBeenNthCalledWith(1, false);
    expect(setConfirmBeforeDelete).toHaveBeenNthCalledWith(2, false);
  });

  it('preserves an edited key-binding draft when delete confirmation finishes saving', async () => {
    const user = userEvent.setup();
    const { rerender } = render(<SettingsModal />);

    await user.click(screen.getByRole('button', { name: 'Next image binding' }));
    await user.keyboard('n');
    await user.click(screen.getByRole('checkbox', { name: 'Confirm before delete' }));
    await waitFor(() => expect(setConfirmBeforeDelete).toHaveBeenCalledWith(false));

    mockSettingsStore(true, false);
    rerender(<SettingsModal />);

    expect(screen.getByRole('button', { name: 'Next image binding' })).toHaveTextContent('N');
    expect(screen.getByRole('checkbox', { name: 'Confirm before delete' })).not.toBeChecked();
  });

  it('does not erase an unsaved binding draft when delayed hydration arrives', async () => {
    const user = userEvent.setup();
    const { rerender } = render(<SettingsModal />);

    await user.click(screen.getByRole('button', { name: 'Next image binding' }));
    await user.keyboard('n');
    expect(screen.getByRole('button', { name: 'Next image binding' })).toHaveTextContent('N');

    mockSettingsStore(true, true, {
      ...DEFAULT_KEY_BINDINGS,
      nextImage: 'x',
      prevImage: 'p',
    });
    rerender(<SettingsModal />);

    expect(screen.getByRole('button', { name: 'Next image binding' })).toHaveTextContent('N');
    expect(screen.getByRole('button', { name: 'Previous image binding' })).toHaveTextContent('P');
    expect(screen.getByRole('button', { name: 'Save key bindings' })).toBeEnabled();
  });

  it('keeps a pending save serialized when Settings closes and reopens', async () => {
    let resolveSave!: (result: { ok: true }) => void;
    setKeyBindings.mockReturnValueOnce(new Promise((resolve) => {
      resolveSave = resolve;
    }));
    const user = userEvent.setup();
    const { rerender } = render(<SettingsModal />);

    await user.click(screen.getByRole('button', { name: 'Next image binding' }));
    await user.keyboard('n');
    await user.click(screen.getByRole('button', { name: 'Save key bindings' }));
    expect(screen.getByRole('button', { name: 'Saving key bindings…' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Close settings' }));
    mockSettingsStore(false);
    rerender(<SettingsModal />);
    mockSettingsStore(true);
    rerender(<SettingsModal />);

    expect(screen.getByRole('button', { name: 'Saving key bindings…' })).toBeDisabled();
    expect(setKeyBindings).toHaveBeenCalledTimes(1);

    await act(async () => {
      resolveSave({ ok: true });
      await Promise.resolve();
    });

    await waitFor(() => expect(screen.getByRole('button', { name: 'Save key bindings' })).toBeEnabled());
    expect(setKeyBindings).toHaveBeenCalledTimes(1);
  });

  it('keeps Escape as a dialog close while capture is active', async () => {
    const user = userEvent.setup();
    render(<SettingsModal />);

    await user.click(screen.getByRole('button', { name: 'Next image binding' }));
    await user.keyboard('{Escape}');

    expect(setShowSettings).toHaveBeenCalledWith(false);
    expect(setKeyBindings).not.toHaveBeenCalled();
  });
});

describe('SettingsModal runtime diagnostics', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockSettingsStore();
    vi.stubGlobal('fetch', vi.fn(async () => runtimeResponse(validRuntimePayload)));
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
  });

  it('fetches only while Settings is open and requests uncached runtime provenance', async () => {
    mockSettingsStore(false);
    const { rerender } = render(<SettingsModal />);
    expect(fetch).not.toHaveBeenCalled();

    mockSettingsStore(true);
    rerender(<SettingsModal />);

    expect(await screen.findByText('Aibos Image')).toBeVisible();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch).toHaveBeenCalledWith('/api/runtime', {
      cache: 'no-store',
      signal: expect.any(AbortSignal),
    });
  });

  it('shows a short revision with the full value in its title and marks dirty builds', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => runtimeResponse({
      ...validRuntimePayload,
      sourceDirty: true,
    })));
    render(<SettingsModal />);

    const revision = await screen.findByText('1234567890');
    expect(revision).toHaveAttribute('title', fullRevision);
    expect(screen.getByText('Dirty')).toHaveClass('is-dirty');
    expect(screen.getByText('build_2026-07-18')).toBeVisible();
    expect(screen.getByText('2026-07-18T01:02:03.000Z')).toHaveAttribute(
      'datetime',
      '2026-07-18T01:02:03.000Z',
    );
    expect(screen.getByText('127.0.0.1:3011')).toBeVisible();
  });

  it('renders launcher-unavailable null fields without blocking normal settings', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => runtimeResponse({
      ...validRuntimePayload,
      sourceRevision: null,
      sourceDirty: null,
      buildId: null,
      buildCompletedAtUtc: null,
      serverHost: null,
      serverPort: null,
    })));
    render(<SettingsModal />);

    const runtimeSection = screen.getByRole('region', { name: 'Runtime / Version' });
    await waitFor(() => expect(runtimeSection).toHaveTextContent('Unavailable'));
    expect(screen.getByRole('definition', { name: 'Source state' })).toHaveTextContent('Unavailable');
    expect(within(runtimeSection).getAllByRole('definition')).toHaveLength(6);
    expect(screen.getByRole('checkbox', { name: 'Show unseen markers' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Save key bindings' })).toBeEnabled();
  });

  it('copies an unavailable source state without claiming the source is clean', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => runtimeResponse({
      ...validRuntimePayload,
      sourceDirty: null,
    })));
    const user = userEvent.setup();
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    writeText.mockResolvedValue(undefined);
    render(<SettingsModal />);

    expect(await screen.findByRole('definition', { name: 'Source state' })).toHaveTextContent('Unavailable');
    await user.click(screen.getByRole('button', { name: 'Copy diagnostics' }));

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1));
    const copied = String(writeText.mock.calls[0][0]);
    expect(copied).toContain('Source state: Unavailable');
    expect(copied).not.toContain('Source state: Clean');
  });

  it('shows invalid and failed payloads inline and recovers through Reload', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(runtimeResponse({
        ...validRuntimePayload,
        sourceRevision: 'C:/Users/private/revision',
      }))
      .mockResolvedValueOnce(runtimeResponse(validRuntimePayload)));
    const user = userEvent.setup();
    render(<SettingsModal />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Runtime details could not be loaded.');
    expect(screen.getByRole('checkbox', { name: 'Confirm before delete' })).toBeEnabled();
    await user.click(screen.getByRole('button', { name: 'Reload' }));

    expect(await screen.findByText('1234567890')).toBeVisible();
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it('copies only safe diagnostics and reports success through the live region', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => runtimeResponse({
      ...validRuntimePayload,
      projectRoot: 'C:/Users/private/project',
      statePath: 'C:/Users/private/.cache/state.json',
    })));
    const user = userEvent.setup();
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    writeText.mockResolvedValue(undefined);
    render(<SettingsModal />);

    await screen.findByText('1234567890');
    await user.click(screen.getByRole('button', { name: 'Copy diagnostics' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Diagnostics copied.');

    const copied = String(writeText.mock.calls[0][0]);
    expect(copied).toContain(`Source revision: ${fullRevision}`);
    expect(copied).toContain('Build ID: build_2026-07-18');
    expect(copied).toContain('Server: 127.0.0.1:3011');
    expect(copied).toContain(`Browser: ${navigator.userAgent}`);
    expect(copied).not.toContain('C:/Users/private');
    expect(copied).not.toContain('4321');
    expect(copied).not.toContain('statePath');
    expect(setView).not.toHaveBeenCalled();
    expect(setKeyBindings).not.toHaveBeenCalled();
    expect(setConfirmBeforeDelete).not.toHaveBeenCalled();
    expect(setShowSettings).not.toHaveBeenCalled();
  });

  it('reports clipboard rejection without closing or disabling Settings', async () => {
    const user = userEvent.setup();
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    writeText.mockRejectedValue(new Error('Permission denied'));
    render(<SettingsModal />);

    await screen.findByText('1234567890');
    await user.click(screen.getByRole('button', { name: 'Copy diagnostics' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not copy diagnostics.');
    expect(screen.getByRole('button', { name: 'Copy diagnostics' })).toBeEnabled();
    expect(screen.getByRole('checkbox', { name: 'Show unseen markers' })).toBeEnabled();
  });

  it('aborts a closed request and ignores its stale response after reopening', async () => {
    let resolveFirst!: (response: Response) => void;
    let firstSignal: AbortSignal | undefined;
    const firstResponse = new Promise<Response>((resolve) => { resolveFirst = resolve; });
    vi.stubGlobal('fetch', vi.fn()
      .mockImplementationOnce((_input: RequestInfo | URL, init?: RequestInit) => {
        firstSignal = init?.signal ?? undefined;
        return firstResponse;
      })
      .mockResolvedValueOnce(runtimeResponse({ ...validRuntimePayload, buildId: 'new-build' })));
    const { rerender } = render(<SettingsModal />);
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));

    mockSettingsStore(false);
    rerender(<SettingsModal />);
    expect(firstSignal?.aborted).toBe(true);

    mockSettingsStore(true);
    rerender(<SettingsModal />);
    expect(await screen.findByText('new-build')).toBeVisible();

    await act(async () => {
      resolveFirst(runtimeResponse({ ...validRuntimePayload, buildId: 'stale-build' }));
      await Promise.resolve();
    });
    expect(screen.queryByText('stale-build')).not.toBeInTheDocument();
    expect(screen.getByText('new-build')).toBeVisible();
  });

  it('keeps only the newest runtime result through repeated open-close churn', async () => {
    const pending: Array<{
      resolve: (response: Response) => void;
      signal?: AbortSignal;
    }> = [];
    vi.stubGlobal('fetch', vi.fn((_input: RequestInfo | URL, init?: RequestInit) => (
      new Promise<Response>((resolve) => {
        pending.push({ resolve, signal: init?.signal ?? undefined });
      })
    )));
    const { rerender } = render(<SettingsModal />);
    await waitFor(() => expect(pending).toHaveLength(1));

    for (let cycle = 0; cycle < 3; cycle += 1) {
      mockSettingsStore(false);
      rerender(<SettingsModal />);
      expect(pending[cycle].signal?.aborted).toBe(true);
      mockSettingsStore(true);
      rerender(<SettingsModal />);
      await waitFor(() => expect(pending).toHaveLength(cycle + 2));
    }

    await act(async () => {
      pending[3].resolve(runtimeResponse({ ...validRuntimePayload, buildId: 'rapid-final-build' }));
    });
    expect(await screen.findByText('rapid-final-build')).toBeVisible();

    await act(async () => {
      pending[2].resolve(runtimeResponse({ ...validRuntimePayload, buildId: 'stale-build-3' }));
      pending[1].resolve(runtimeResponse({ ...validRuntimePayload, buildId: 'stale-build-2' }));
      pending[0].resolve(runtimeResponse({ ...validRuntimePayload, buildId: 'stale-build-1' }));
      await Promise.resolve();
    });
    expect(screen.getByText('rapid-final-build')).toBeVisible();
    expect(screen.queryByText(/stale-build-/)).not.toBeInTheDocument();
  });

  it('aborts the runtime request when the Settings component unmounts', async () => {
    let requestSignal: AbortSignal | undefined;
    let resolveRequest!: (response: Response) => void;
    vi.stubGlobal('fetch', vi.fn((_input: RequestInfo | URL, init?: RequestInit) => {
      requestSignal = init?.signal ?? undefined;
      return new Promise<Response>((resolve) => { resolveRequest = resolve; });
    }));
    const view = render(<SettingsModal />);
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));

    view.unmount();
    expect(requestSignal?.aborted).toBe(true);
    await act(async () => {
      resolveRequest(runtimeResponse(validRuntimePayload));
      await Promise.resolve();
    });
  });

  it('keeps runtime actions in the dialog keyboard sequence', async () => {
    const user = userEvent.setup();
    render(<SettingsModal />);

    await screen.findByText('1234567890');
    expect(screen.getByRole('button', { name: 'Close settings' })).toHaveFocus();
    await user.tab();
    expect(screen.getByRole('button', { name: 'Reload' })).toHaveFocus();
    await user.tab();
    expect(screen.getByRole('button', { name: 'Copy diagnostics' })).toHaveFocus();
  });
});
