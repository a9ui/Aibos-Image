import React from "react";
import { readFileSync } from "node:fs";
import path from "node:path";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { DEFAULT_THUMBNAIL_STATUS_BORDERS, type ImageFile } from "../lib/types";
import { useOptionalAlbumStore } from "../store/AlbumContext";
import { useImageStore } from "../store/ImageContext";
import ImageGrid from "./ImageGrid";

vi.mock("../store/ImageContext", () => ({
  useImageStore: vi.fn(),
}));

vi.mock("../store/AlbumContext", () => ({
  useOptionalAlbumStore: vi.fn(),
}));

vi.mock("./CachedImage", () => ({
  default: ({ alt }: { alt: string }) => <span data-image-alt={alt} />,
}));

const firstImage: ImageFile = {
  id: "C:/images/first.png",
  filename: "first.png",
  absolutePath: "C:/images/first.png",
  fileUrl: "/api/image?first",
  displayUrl: "/api/image?first&display=1",
  fullUrl: "/api/image?first&full=1",
  metadata: { prompt: "first prompt", negativePrompt: "", settings: {} },
  createdAt: 1,
  mtime: 1,
};

const secondImage: ImageFile = {
  ...firstImage,
  id: "C:/images/second.png",
  filename: "second.png",
  absolutePath: "C:/images/second.png",
  fileUrl: "/api/image?second",
  displayUrl: "/api/image?second&display=1",
  fullUrl: "/api/image?second&full=1",
};

function pagedImage(index: number): ImageFile {
  return {
    ...firstImage,
    id: `C:/images/paged-${index}.png`,
    filename: `paged-${index}.png`,
    absolutePath: `C:/images/paged-${index}.png`,
    fileUrl: `/api/image?paged=${index}`,
    displayUrl: `/api/image?paged=${index}&display=1`,
    fullUrl: `/api/image?paged=${index}&full=1`,
  };
}

function pagedResults(total: number, loaded: number) {
  return Array.from({ length: total }, (_, index) => index < loaded ? pagedImage(index) : null);
}

const selectImage = vi.fn();
const openPreviewTab = vi.fn();
const openModalAtImage = vi.fn();
const cycleFavoriteLevel = vi.fn();
const decreaseFavoriteLevel = vi.fn();
const markImageSeen = vi.fn();
const requestRevealImage = vi.fn();
const retrySearch = vi.fn();
const rescanExpiredSearchSession = vi.fn();
const dismissSearchError = vi.fn();
const clearSelection = vi.fn();
let mockClientWidth = 960;
let mockClientHeight = 640;

function createStore(
  viewMode: "grid" | "list" = "grid",
  options: { selectedIds?: string[]; revealImageId?: string | null } = {},
) {
  return {
    searchQuery: "",
    searchResults: [firstImage, secondImage],
    searchTotal: 2,
    isSearching: false,
    ensureSearchRange: vi.fn(),
    selectImage,
    openPreviewTab,
    cycleFavoriteLevel,
    decreaseFavoriteLevel,
    favorites: {},
    view: {
      viewMode,
      thumbSize: 200,
      columns: 0,
      aspectMode: "original",
      displayStyle: "standard",
      sortBy: "newest",
      randomSeed: "",
      dateFrom: "",
      dateTo: "",
      hiddenFolders: [],
      showUnseenMarkers: false,
    },
    setView: vi.fn(),
    selectedIds: options.selectedIds ?? [],
    showFavOnly: false,
    showUnfavOnly: false,
    favoriteFilterLevels: [],
    showEnhancedOnly: false,
    enhancedSourceIds: {},
    thumbnailStatusBorders: DEFAULT_THUMBNAIL_STATUS_BORDERS,
    closeAllPreviews: vi.fn(),
    clearSelection,
    setSearchScrollPosition: vi.fn(),
    getSearchScrollPosition: () => null,
    seenImageIds: {},
    markImageSeen,
    revealImageId: options.revealImageId ?? null,
    consumeRevealImage: vi.fn(),
    openModalAtImage,
    modalImageIds: [],
    setModalImageIds: vi.fn(),
    selectedIndex: null,
    setSelectedIndex: vi.fn(),
    requestRevealImage,
    showSettings: false,
    dirPath: "C:/images",
  } as unknown as ReturnType<typeof useImageStore>;
}

function renderGrid() {
  return render(
    <div className="viewer-main" data-testid="viewer-main">
      <ImageGrid />
    </div>,
  );
}

beforeAll(() => {
  class ResizeObserverMock {
    observe() {}
    disconnect() {}
  }
  vi.stubGlobal("ResizeObserver", ResizeObserverMock);
  vi.stubGlobal(
    "fetch",
    vi.fn(() => Promise.resolve(new Response("{}"))),
  );
  Object.defineProperties(HTMLElement.prototype, {
    clientHeight: { configurable: true, get: () => mockClientHeight },
    clientWidth: { configurable: true, get: () => mockClientWidth },
  });
});

beforeEach(() => {
  vi.clearAllMocks();
  mockClientWidth = 960;
  mockClientHeight = 640;
  vi.mocked(useImageStore).mockReturnValue(createStore());
  vi.mocked(useOptionalAlbumStore).mockReturnValue({
    activeSource: null,
    refreshActiveSource: vi.fn(),
  } as unknown as ReturnType<typeof useOptionalAlbumStore>);
});

describe("ImageGrid keyboard primary controls", () => {
  it("announces a search error above retained results and retries without showing the empty state", async () => {
    vi.mocked(useImageStore).mockReturnValue({
      ...createStore(),
      searchError: "Search service temporarily unavailable",
      retrySearch,
      dismissSearchError,
    });

    const user = userEvent.setup();
    renderGrid();

    expect(screen.getByRole("alert")).toHaveTextContent("Search error: Search service temporarily unavailable");
    expect(screen.getByRole("button", { name: /select first\.png/i })).toBeInTheDocument();
    expect(screen.queryByText(/no images match/i)).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Retry the current search" }));
    expect(retrySearch).toHaveBeenCalledTimes(1);
  });

  it('uses Rescan rather than Retry for an expired index session while retaining the gallery', async () => {
    vi.mocked(useImageStore).mockReturnValue({
      ...createStore(),
      searchError: 'This viewer session expired. Scan the folder set again to refresh it.',
      searchErrorKind: 'session-expired',
      retrySearch,
      rescanExpiredSearchSession,
      dismissSearchError,
    });
    const user = userEvent.setup();
    renderGrid();

    expect(screen.getByRole('alert')).toHaveTextContent('Session expired: This viewer session expired.');
    expect(screen.getByRole('button', { name: /select first\.png/i })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Rescan the current folder set to refresh the viewer session' }));
    expect(rescanExpiredSearchSession).toHaveBeenCalledTimes(1);
    expect(retrySearch).not.toHaveBeenCalled();
  });

  it("reaches the first card by keyboard, selects with Space, and opens with Enter", async () => {
    const user = userEvent.setup();
    renderGrid();

    await user.tab();
    const primary = screen.getByRole("button", { name: /select first\.png/i });
    expect(primary).toHaveFocus();
    expect(primary).toHaveAttribute("aria-pressed", "false");

    await user.keyboard(" ");
    expect(markImageSeen).toHaveBeenCalledWith(firstImage.id);
    expect(selectImage).toHaveBeenCalledWith(
      firstImage,
      [firstImage.id, secondImage.id],
      { range: false, toggle: false },
    );

    await user.keyboard("{Enter}");
    expect(openPreviewTab).toHaveBeenCalledWith(firstImage, {
      makeActive: true,
      pin: true,
    });
    expect(openModalAtImage).toHaveBeenCalledWith(firstImage.id, 0, []);
  });

  it("keeps favorite controls independent and preserves Ctrl/Shift selection flags", async () => {
    const user = userEvent.setup();
    renderGrid();

    const favorite = screen.getByRole("button", {
      name: /increase favorite level for first\.png/i,
    });
    await user.click(favorite);
    expect(cycleFavoriteLevel).toHaveBeenCalledWith(firstImage.id);
    expect(markImageSeen).not.toHaveBeenCalled();

    const primary = screen.getByRole("button", { name: /select first\.png/i });
    fireEvent.keyDown(primary, { key: " ", ctrlKey: true });
    expect(selectImage).toHaveBeenLastCalledWith(
      firstImage,
      [firstImage.id, secondImage.id],
      { range: false, toggle: true },
    );
    fireEvent.keyDown(primary, { key: " ", shiftKey: true });
    expect(selectImage).toHaveBeenLastCalledWith(
      firstImage,
      [firstImage.id, secondImage.id],
      { range: true, toggle: false },
    );
  });

  it("uses a group plus sibling primary and favorite buttons in both grid and list views", () => {
    const { rerender } = renderGrid();
    expect(
      screen.getByRole("group", { name: "Image first.png" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /select first\.png/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", {
        name: /increase favorite level for first\.png/i,
      }),
    ).toBeInTheDocument();

    vi.mocked(useImageStore).mockReturnValue(createStore("list"));
    rerender(
      <div className="viewer-main">
        <ImageGrid />
      </div>,
    );
    expect(
      screen.getByRole("group", { name: "Image first.png" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /select first\.png/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", {
        name: /increase favorite level for first\.png/i,
      }),
    ).toBeInTheDocument();
  });

  it.each(["grid", "list"] as const)(
    "renders the default enhanced cyan outer ring with the favorite yellow inner ring in %s view",
    (viewMode) => {
      vi.mocked(useImageStore).mockReturnValue({
        ...createStore(viewMode),
        favorites: { [firstImage.id]: 2 },
        enhancedSourceIds: { [firstImage.id]: true },
        thumbnailStatusBorders: DEFAULT_THUMBNAIL_STATUS_BORDERS,
      });
      renderGrid();

      const overlay = screen.getByTestId("thumbnail-status-borders");
      expect(overlay).toHaveAttribute("data-favorite-border", "#facc15");
      expect(overlay).toHaveAttribute("data-enhanced-border", "#38bdf8");
      expect(overlay).toHaveAttribute("data-enhanced-border-mode", "solid");
      expect(overlay.className).toContain("hasFavorite");
      expect(overlay.className).toContain("hasEnhanced");
      expect(overlay.className).not.toContain("enhancedRainbow");
      expect(overlay.style.getPropertyValue("--favorite-thumbnail-border-color")).toBe("#facc15");
      expect(overlay.style.getPropertyValue("--enhanced-thumbnail-border-color")).toBe("#38bdf8");
    },
  );

  it("keeps custom favorite and enhanced thumbnail borders independently visible when combined", () => {
    const store = createStore("grid", { selectedIds: [firstImage.id] });
    vi.mocked(useImageStore).mockReturnValue({
      ...store,
      view: { ...store.view, showUnseenMarkers: true },
      favorites: { [firstImage.id]: 3 },
      enhancedSourceIds: { [firstImage.id]: true },
      thumbnailStatusBorders: {
        favorite: { enabled: true, color: "#112233" },
        enhanced: { enabled: true, color: "#abcdef" },
      },
    });
    renderGrid();

    const firstGroup = screen.getByRole("group", { name: "Image first.png" });
    const overlay = screen.getByTestId("thumbnail-status-borders");
    expect(overlay).toHaveAttribute("data-favorite-border", "#112233");
    expect(overlay).toHaveAttribute("data-enhanced-border", "#abcdef");
    expect(overlay).toHaveAttribute("data-enhanced-border-mode", "solid");
    expect(overlay.className).not.toContain("enhancedRainbow");
    expect(overlay.style.getPropertyValue("--enhanced-thumbnail-border-color")).toBe("#abcdef");
    expect(firstGroup).toHaveClass("is-selected");
    expect(firstGroup).toHaveClass("is-unseen");
  });

  it("keeps the favorite thumbnail ring on the inner inset even without an enhanced ring", () => {
    const css = readFileSync(
      path.join(process.cwd(), "src/components/ImageGridStatusBorders.module.css"),
      "utf8",
    );

    expect(css).toMatch(/\.hasFavorite::after\s*\{[\s\S]*?inset:\s*4px;/);
    expect(css).not.toMatch(/\.hasEnhanced\.hasFavorite::after/);
  });

  it.each(["grid", "list"] as const)(
    "hides a disabled favorite border without hiding the enhanced border in %s view",
    (viewMode) => {
      vi.mocked(useImageStore).mockReturnValue({
        ...createStore(viewMode),
        favorites: { [firstImage.id]: 5 },
        enhancedSourceIds: { [firstImage.id]: true },
        thumbnailStatusBorders: {
          favorite: { enabled: false, color: "#112233" },
          enhanced: { enabled: true, color: "#abcdef" },
        },
      });
      renderGrid();

      const overlay = screen.getByTestId("thumbnail-status-borders");
      expect(overlay).not.toHaveAttribute("data-favorite-border");
      expect(overlay).toHaveAttribute("data-enhanced-border", "#abcdef");
    },
  );

  it("moves the roving primary target with arrows and focuses it after the requested reveal", async () => {
    const user = userEvent.setup();
    const store = createStore("list", { selectedIds: [firstImage.id] });
    vi.mocked(useImageStore).mockReturnValue(store);
    const { rerender } = renderGrid();

    const firstPrimary = screen.getByRole("button", {
      name: /select first\.png/i,
    });
    act(() => firstPrimary.focus());
    await user.keyboard("{ArrowDown}");
    expect(requestRevealImage).toHaveBeenCalledWith(secondImage.id);
    expect(
      screen.getByRole("button", { name: /select second\.png/i }),
    ).toHaveAttribute("tabindex", "0");

    vi.mocked(useImageStore).mockReturnValue(
      createStore("list", {
        selectedIds: [secondImage.id],
        revealImageId: secondImage.id,
      }),
    );
    rerender(
      <div className="viewer-main">
        <ImageGrid />
      </div>,
    );
    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /select second\.png/i }),
      ).toHaveFocus();
    });
  });

  it.each(["grid", "list"] as const)("clears selection from %s background but not image controls", (viewMode) => {
    vi.mocked(useImageStore).mockReturnValue(createStore(viewMode, { selectedIds: [firstImage.id] }));
    renderGrid();
    const canvas = screen.getByTestId('image-grid-background');
    fireEvent.click(canvas);
    expect(clearSelection).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: /select first\.png/i }));
    expect(clearSelection).toHaveBeenCalledTimes(1);
  });

  it("keeps Alt+wheel as native scroll and reserves gallery zoom for Ctrl/Cmd+wheel", () => {
    const store = createStore();
    vi.mocked(useImageStore).mockReturnValue(store);
    renderGrid();
    const canvas = screen.getByTestId("image-grid-background");

    const altWheel = new WheelEvent("wheel", {
      altKey: true,
      bubbles: true,
      cancelable: true,
      deltaY: -100,
    });
    fireEvent(canvas, altWheel);
    expect(altWheel.defaultPrevented).toBe(false);
    expect(store.setView).not.toHaveBeenCalled();

    const ctrlWheel = new WheelEvent("wheel", {
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
      deltaY: -100,
    });
    fireEvent(canvas, ctrlWheel);
    expect(ctrlWheel.defaultPrevented).toBe(true);
    expect(store.setView).toHaveBeenCalledWith({ thumbSize: 220 });
  });

  it("flushes visible thumbnail warmup immediately instead of waiting behind nearby work", () => {
    renderGrid();

    const visibleWarmup = vi.mocked(fetch).mock.calls.find(([url, init]) => {
      if (url !== "/api/thumbs/warm" || typeof init?.body !== "string") return false;
      return JSON.parse(init.body).priority === "visible";
    });
    expect(visibleWarmup).toBeDefined();
  });

  it.each(["favorite", "unrated", "enhanced"] as const)(
    "keeps scanning source pages when the first loaded page has no %s matches",
    async (filterMode) => {
      const ensureSearchRange = vi.fn();
      const loaded = pagedResults(500, 100);
      const rated = Object.fromEntries(
        loaded.slice(0, 100).map((image) => [(image as ImageFile).id, 1]),
      );
      vi.mocked(useImageStore).mockReturnValue({
        ...createStore(),
        searchResults: loaded,
        searchTotal: 500,
        ensureSearchRange,
        showFavOnly: filterMode === "favorite",
        showUnfavOnly: filterMode === "unrated",
        showEnhancedOnly: filterMode === "enhanced",
        favorites: filterMode === "unrated" ? rated : {},
        enhancedSourceIds: {},
      } as unknown as ReturnType<typeof useImageStore>);

      renderGrid();

      await waitFor(() => expect(ensureSearchRange).toHaveBeenCalledWith(100, 299));
    },
  );

  it("keeps a bottom paging demand across sparse batches until the final match is reachable", async () => {
    mockClientWidth = 100;
    mockClientHeight = 1;
    const ensureSearchRange = vi.fn();
    const favorites: Record<string, number> = {};
    for (let index = 0; index < 30; index += 1) favorites[pagedImage(index).id] = 1;
    const baseStore = {
      ...createStore(),
      searchResults: pagedResults(600, 200),
      searchTotal: 600,
      ensureSearchRange,
      showFavOnly: true,
      favorites,
    } as unknown as ReturnType<typeof useImageStore>;
    vi.mocked(useImageStore).mockReturnValue(baseStore);

    const { rerender } = renderGrid();
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 30)); });
    expect(ensureSearchRange).not.toHaveBeenCalled();

    const scrollElement = screen.getByTestId("viewer-main");
    scrollElement.scrollTop = 4700;
    fireEvent.scroll(scrollElement);
    await waitFor(() => expect(ensureSearchRange).toHaveBeenCalledWith(200, 399));

    ensureSearchRange.mockClear();
    for (let index = 200; index < 210; index += 1) favorites[pagedImage(index).id] = 1;
    vi.mocked(useImageStore).mockReturnValue({
      ...baseStore,
      searchResults: pagedResults(600, 400),
      favorites: { ...favorites },
    });
    rerender(
      <div className="viewer-main" data-testid="viewer-main">
        <ImageGrid />
      </div>,
    );

    // The ten new sparse matches move the new bottom beyond the old scroll
    // position. The retained demand must still request the next batch.
    await waitFor(() => expect(ensureSearchRange).toHaveBeenCalledWith(400, 599));

    ensureSearchRange.mockClear();
    favorites[pagedImage(599).id] = 1;
    vi.mocked(useImageStore).mockReturnValue({
      ...baseStore,
      searchResults: pagedResults(600, 600),
      favorites: { ...favorites },
    });
    rerender(
      <div className="viewer-main" data-testid="viewer-main">
        <ImageGrid />
      </div>,
    );
    expect(ensureSearchRange).not.toHaveBeenCalled();

    scrollElement.scrollTop = 6500;
    fireEvent.scroll(scrollElement);
    expect(await screen.findByRole("button", { name: /select paged-599\.png/i })).toBeInTheDocument();
  });

  it("resets retained paging demand when the index/filter context becomes stale or is cancelled", async () => {
    mockClientWidth = 100;
    mockClientHeight = 1;
    const ensureSearchRange = vi.fn();
    const favorites = Object.fromEntries(
      Array.from({ length: 30 }, (_, index) => [pagedImage(index).id, 1]),
    );
    const baseStore = {
      ...createStore(),
      searchResults: pagedResults(600, 200),
      searchTotal: 600,
      ensureSearchRange,
      showFavOnly: true,
      favorites,
    } as unknown as ReturnType<typeof useImageStore>;
    vi.mocked(useImageStore).mockReturnValue(baseStore);
    const { rerender } = renderGrid();
    const scrollElement = screen.getByTestId("viewer-main");

    scrollElement.scrollTop = 4700;
    fireEvent.scroll(scrollElement);
    await waitFor(() => expect(ensureSearchRange).toHaveBeenCalledWith(200, 399));

    scrollElement.scrollTop = 0;
    fireEvent.scroll(scrollElement);
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 30)); });
    ensureSearchRange.mockClear();
    vi.mocked(useImageStore).mockReturnValue({
      ...baseStore,
      indexToken: "fresh-index-token",
      favoriteFilterLevels: [1],
    });
    rerender(
      <div className="viewer-main" data-testid="viewer-main">
        <ImageGrid />
      </div>,
    );
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 30)); });
    expect(ensureSearchRange).not.toHaveBeenCalled();

    ensureSearchRange.mockClear();
    vi.mocked(useImageStore).mockReturnValue({
      ...baseStore,
      showFavOnly: false,
    });
    rerender(
      <div className="viewer-main" data-testid="viewer-main">
        <ImageGrid />
      </div>,
    );
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 30)); });
    expect(ensureSearchRange).toHaveBeenCalledWith(0, 5);
    expect(ensureSearchRange).not.toHaveBeenCalledWith(200, 399);
  });

  it("captures Ctrl/Cmd wheel only inside the grid and changes thumbnail size", () => {
    const store = createStore();
    vi.mocked(useImageStore).mockReturnValue(store);
    renderGrid();
    const primary = screen.getByRole("button", { name: /select first\.png/i });
    const card = screen.getByRole("group", { name: "Image first.png" });
    expect(card).toHaveAttribute("data-grid-index", "0");

    const wheel = new WheelEvent("wheel", {
      bubbles: true,
      cancelable: true,
      ctrlKey: true,
      deltaY: -120,
    });
    fireEvent(primary, wheel);

    expect(wheel.defaultPrevented).toBe(true);
    expect(store.setView).toHaveBeenCalledWith({ thumbSize: 220 });
  });

  it("keeps the wheel-targeted image at the same viewport offset when zoom changes the column count", async () => {
    mockClientWidth = 900;
    const images = Array.from({ length: 80 }, (_, index): ImageFile => ({
      ...firstImage,
      id: `C:/images/zoom-${index}.png`,
      filename: `zoom-${index}.png`,
      absolutePath: `C:/images/zoom-${index}.png`,
      fileUrl: `/api/image?zoom=${index}`,
      displayUrl: `/api/image?zoom=${index}&display=1`,
      fullUrl: `/api/image?zoom=${index}&full=1`,
    }));
    const initialStore = {
      ...createStore(),
      searchResults: images,
      searchTotal: images.length,
    } as unknown as ReturnType<typeof useImageStore>;
    vi.mocked(useImageStore).mockReturnValue(initialStore);

    const { rerender } = renderGrid();
    const scrollElement = screen.getByTestId("viewer-main");
    Object.defineProperty(scrollElement, "scrollHeight", {
      configurable: true,
      get: () => 20_000,
    });
    scrollElement.scrollTop = 600;
    fireEvent.scroll(scrollElement);

    await waitFor(() => {
      expect(initialStore.setSearchScrollPosition).toHaveBeenCalledWith(
        expect.any(String),
        600,
      );
    });
    const targetBefore = screen.getByRole("group", { name: "Image zoom-10.png" });
    expect(targetBefore).toHaveStyle({ top: "672px" });
    const offsetBefore = Number.parseFloat(targetBefore.style.top) - scrollElement.scrollTop;

    const wheel = new WheelEvent("wheel", {
      bubbles: true,
      cancelable: true,
      ctrlKey: true,
      deltaY: -120,
    });
    fireEvent(
      screen.getByRole("button", { name: /select zoom-10\.png/i }),
      wheel,
    );
    expect(wheel.defaultPrevented).toBe(true);
    expect(initialStore.setView).toHaveBeenCalledWith({ thumbSize: 220 });

    vi.mocked(useImageStore).mockReturnValue({
      ...initialStore,
      view: { ...initialStore.view, thumbSize: 220 },
    });
    rerender(
      <div className="viewer-main" data-testid="viewer-main">
        <ImageGrid />
      </div>,
    );

    await waitFor(() => expect(scrollElement.scrollTop).toBe(1278));
    const targetAfter = screen.getByRole("group", { name: "Image zoom-10.png" });
    expect(targetAfter).toHaveStyle({ top: "1350px" });
    expect(Number.parseFloat(targetAfter.style.top) - scrollElement.scrollTop).toBe(offsetBefore);
  });

  it("keeps catalog and each Album in separate scroll-memory contexts", async () => {
    const getSearchScrollPosition = vi.fn<(key: string) => number | null>(() => null);
    const store = {
      ...createStore(),
      getSearchScrollPosition,
    } as unknown as ReturnType<typeof useImageStore>;
    vi.mocked(useImageStore).mockReturnValue(store);
    const refreshActiveSource = vi.fn();
    const albumContext = (albumId: string | null) => ({
      activeSource: albumId
        ? {
          album: { id: albumId },
          images: [firstImage, secondImage],
          sourceToken: `source-${albumId}`,
        }
        : null,
      refreshActiveSource,
    } as unknown as ReturnType<typeof useOptionalAlbumStore>);

    vi.mocked(useOptionalAlbumStore).mockReturnValue(albumContext(null));
    const { rerender } = renderGrid();
    await waitFor(() => expect(getSearchScrollPosition).toHaveBeenCalledTimes(1));
    const catalogKey = getSearchScrollPosition.mock.calls[0][0];

    vi.mocked(useOptionalAlbumStore).mockReturnValue(albumContext("album-a"));
    rerender(<div className="viewer-main" data-testid="viewer-main"><ImageGrid /></div>);
    await waitFor(() => expect(getSearchScrollPosition).toHaveBeenCalledTimes(2));
    const albumAKey = getSearchScrollPosition.mock.calls[1][0];

    vi.mocked(useOptionalAlbumStore).mockReturnValue(albumContext("album-b"));
    rerender(<div className="viewer-main" data-testid="viewer-main"><ImageGrid /></div>);
    await waitFor(() => expect(getSearchScrollPosition).toHaveBeenCalledTimes(3));
    const albumBKey = getSearchScrollPosition.mock.calls[2][0];

    vi.mocked(useOptionalAlbumStore).mockReturnValue(albumContext(null));
    rerender(<div className="viewer-main" data-testid="viewer-main"><ImageGrid /></div>);
    await waitFor(() => expect(getSearchScrollPosition).toHaveBeenCalledTimes(4));

    expect(JSON.parse(catalogKey)).toMatchObject({ source: { kind: "catalog" } });
    expect(JSON.parse(albumAKey)).toMatchObject({ source: { kind: "album", id: "album-a" } });
    expect(JSON.parse(albumBKey)).toMatchObject({ source: { kind: "album", id: "album-b" } });
    expect(new Set([catalogKey, albumAKey, albumBKey]).size).toBe(3);
    expect(getSearchScrollPosition.mock.calls[3][0]).toBe(catalogKey);
  });

  it("keeps the visible selected image anchored when the sidebar slider changes thumbnail size", async () => {
    mockClientWidth = 900;
    const images = Array.from({ length: 80 }, (_, index): ImageFile => ({
      ...firstImage,
      id: `C:/images/selected-zoom-${index}.png`,
      filename: `selected-zoom-${index}.png`,
      absolutePath: `C:/images/selected-zoom-${index}.png`,
      fileUrl: `/api/image?selected-zoom=${index}`,
      displayUrl: `/api/image?selected-zoom=${index}&display=1`,
      fullUrl: `/api/image?selected-zoom=${index}&full=1`,
    }));
    const selectedImage = images[14];
    const initialStore = {
      ...createStore("grid", { selectedIds: [selectedImage.id] }),
      searchResults: images,
      searchTotal: images.length,
    } as unknown as ReturnType<typeof useImageStore>;
    vi.mocked(useImageStore).mockReturnValue(initialStore);

    const { rerender } = renderGrid();
    const scrollElement = screen.getByTestId("viewer-main");
    Object.defineProperty(scrollElement, "scrollHeight", {
      configurable: true,
      get: () => 20_000,
    });
    scrollElement.scrollTop = 600;
    fireEvent.scroll(scrollElement);
    await waitFor(() => {
      expect(initialStore.setSearchScrollPosition).toHaveBeenCalledWith(
        expect.any(String),
        600,
      );
    });

    const selectedBefore = screen.getByRole("group", { name: `Image ${selectedImage.filename}` });
    const offsetBefore = Number.parseFloat(selectedBefore.style.top) - scrollElement.scrollTop;

    vi.mocked(useImageStore).mockReturnValue({
      ...initialStore,
      view: { ...initialStore.view, thumbSize: 220 },
    });
    rerender(
      <div className="viewer-main" data-testid="viewer-main">
        <ImageGrid />
      </div>,
    );

    await waitFor(() => {
      const selectedAfter = screen.getByRole("group", { name: `Image ${selectedImage.filename}` });
      expect(Number.parseFloat(selectedAfter.style.top) - scrollElement.scrollTop).toBe(offsetBefore);
    });
  });

  it("keeps the visible selected image anchored when sidebar geometry expands and contracts", async () => {
    mockClientWidth = 900;
    const images = Array.from({ length: 80 }, (_, index): ImageFile => ({
      ...firstImage,
      id: `C:/images/sidebar-anchor-${index}.png`,
      filename: `sidebar-anchor-${index}.png`,
      absolutePath: `C:/images/sidebar-anchor-${index}.png`,
      fileUrl: `/api/image?sidebar-anchor=${index}`,
      displayUrl: `/api/image?sidebar-anchor=${index}&display=1`,
      fullUrl: `/api/image?sidebar-anchor=${index}&full=1`,
    }));
    const selectedImage = images[14];
    const store = {
      ...createStore("grid", { selectedIds: [selectedImage.id] }),
      searchResults: images,
      searchTotal: images.length,
    } as unknown as ReturnType<typeof useImageStore>;
    vi.mocked(useImageStore).mockReturnValue(store);

    renderGrid();
    const scrollElement = screen.getByTestId("viewer-main");
    Object.defineProperty(scrollElement, "scrollHeight", {
      configurable: true,
      get: () => 20_000,
    });
    scrollElement.scrollTop = 600;
    fireEvent.scroll(scrollElement);
    await waitFor(() => {
      expect(store.setSearchScrollPosition).toHaveBeenCalledWith(expect.any(String), 600);
    });

    const offsetBefore = Number.parseFloat(
      screen.getByRole("group", { name: `Image ${selectedImage.filename}` }).style.top,
    ) - scrollElement.scrollTop;

    mockClientWidth = 1140;
    fireEvent(window, new Event("resize"));
    await waitFor(() => {
      const selected = screen.getByRole("group", { name: `Image ${selectedImage.filename}` });
      expect(Number.parseFloat(selected.style.top) - scrollElement.scrollTop).toBe(offsetBefore);
    });

    mockClientWidth = 900;
    fireEvent(window, new Event("resize"));
    await waitFor(() => {
      const selected = screen.getByRole("group", { name: `Image ${selectedImage.filename}` });
      expect(Number.parseFloat(selected.style.top) - scrollElement.scrollTop).toBe(offsetBefore);
    });
  });

  it("captures gallery zoom across the grid viewer so the sidebar does not page-zoom, but leaves list mode native", () => {
    const gridStore = createStore();
    vi.mocked(useImageStore).mockReturnValue(gridStore);
    const { unmount } = render(
      <div>
        <button type="button">Outside gallery</button>
        <div className="viewer-main"><ImageGrid /></div>
      </div>,
    );
    const outsideWheel = new WheelEvent("wheel", {
      bubbles: true,
      cancelable: true,
      ctrlKey: true,
      deltaY: -120,
    });
    fireEvent(screen.getByRole("button", { name: "Outside gallery" }), outsideWheel);
    expect(outsideWheel.defaultPrevented).toBe(true);
    expect(gridStore.setView).toHaveBeenCalledWith({ thumbSize: 220 });
    unmount();

    const listStore = createStore("list");
    vi.mocked(useImageStore).mockReturnValue(listStore);
    renderGrid();
    const listWheel = new WheelEvent("wheel", {
      bubbles: true,
      cancelable: true,
      ctrlKey: true,
      deltaY: -120,
    });
    fireEvent(screen.getByRole("button", { name: /select first\.png/i }), listWheel);
    expect(listWheel.defaultPrevented).toBe(false);
    expect(listStore.setView).not.toHaveBeenCalled();
  });

  it("maps Ctrl/Cmd keyboard zoom to gallery thumbnails even when the selected image button has focus", () => {
    const store = createStore();
    vi.mocked(useImageStore).mockReturnValue(store);
    renderGrid();
    const selectedButton = screen.getByRole("button", { name: /select first\.png/i });
    act(() => selectedButton.focus());
    const keydown = new KeyboardEvent("keydown", {
      bubbles: true,
      cancelable: true,
      ctrlKey: true,
      key: "+",
    });
    fireEvent(selectedButton, keydown);

    expect(keydown.defaultPrevented).toBe(true);
    expect(store.setView).toHaveBeenCalledWith({ thumbSize: 220 });
  });

  it("keeps keyboard gallery zoom away from text editing and supports Ctrl/Cmd+0 reset", () => {
    const store = {
      ...createStore(),
      view: { ...createStore().view, thumbSize: 260 },
    } as unknown as ReturnType<typeof useImageStore>;
    vi.mocked(useImageStore).mockReturnValue(store);
    render(
      <div>
        <input aria-label="Search editor" />
        <div className="viewer-main"><ImageGrid /></div>
      </div>,
    );

    const input = screen.getByRole("textbox", { name: "Search editor" });
    const editingZoom = new KeyboardEvent("keydown", {
      bubbles: true,
      cancelable: true,
      ctrlKey: true,
      key: "+",
    });
    fireEvent(input, editingZoom);
    expect(editingZoom.defaultPrevented).toBe(false);
    expect(store.setView).not.toHaveBeenCalled();

    const reset = new KeyboardEvent("keydown", {
      bubbles: true,
      cancelable: true,
      ctrlKey: true,
      key: "0",
    });
    fireEvent(window, reset);
    expect(reset.defaultPrevented).toBe(true);
    expect(store.setView).toHaveBeenCalledWith({ thumbSize: 200 });
  });

  it("does not take Ctrl/Cmd wheel from text editing or an active confirmation overlay", () => {
    const store = createStore();
    vi.mocked(useImageStore).mockReturnValue(store);
    render(
      <div>
        <input aria-label="Search editor" />
        <div className="viewer-main"><ImageGrid /></div>
      </div>,
    );

    const editingWheel = new WheelEvent("wheel", {
      bubbles: true,
      cancelable: true,
      ctrlKey: true,
      deltaY: -120,
    });
    fireEvent(screen.getByRole("textbox", { name: "Search editor" }), editingWheel);
    expect(editingWheel.defaultPrevented).toBe(false);
    expect(store.setView).not.toHaveBeenCalled();

    const confirmOverlay = document.createElement("div");
    confirmOverlay.className = "confirm-overlay";
    document.body.appendChild(confirmOverlay);
    const overlayWheel = new WheelEvent("wheel", {
      bubbles: true,
      cancelable: true,
      ctrlKey: true,
      deltaY: -120,
    });
    fireEvent(screen.getByRole("group", { name: "Image first.png" }), overlayWheel);
    expect(store.setView).not.toHaveBeenCalled();
    confirmOverlay.remove();
  });
});
