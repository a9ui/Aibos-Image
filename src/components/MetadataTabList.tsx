import React, { useRef } from 'react';

export const METADATA_TABS = [
  { id: 'prompt', label: 'Prompt' },
  { id: 'negative', label: 'Negative' },
  { id: 'settings', label: 'Settings' },
] as const;

export type MetadataTab = (typeof METADATA_TABS)[number]['id'];

interface MetadataTabListProps {
  activeTab: MetadataTab;
  onActiveTabChange: (tab: MetadataTab) => void;
  panelId: string;
}

export function MetadataTabList({ activeTab, onActiveTabChange, panelId }: MetadataTabListProps) {
  const tabRefs = useRef<Record<MetadataTab, HTMLButtonElement | null>>({
    prompt: null,
    negative: null,
    settings: null,
  });

  const activateAndFocus = (tab: MetadataTab) => {
    onActiveTabChange(tab);
    tabRefs.current[tab]?.focus();
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>, tab: MetadataTab) => {
    const currentIndex = METADATA_TABS.findIndex((item) => item.id === tab);
    if (currentIndex < 0) return;
    let nextIndex: number | null = null;
    if (event.key === 'ArrowRight') nextIndex = (currentIndex + 1) % METADATA_TABS.length;
    else if (event.key === 'ArrowLeft') nextIndex = (currentIndex - 1 + METADATA_TABS.length) % METADATA_TABS.length;
    else if (event.key === 'Home') nextIndex = 0;
    else if (event.key === 'End') nextIndex = METADATA_TABS.length - 1;
    if (nextIndex === null) return;

    event.preventDefault();
    activateAndFocus(METADATA_TABS[nextIndex].id);
  };

  return (
    <div className="sidebar-tabs" role="tablist" aria-label="Image metadata">
      {METADATA_TABS.map((tab) => (
        <button
          key={tab.id}
          ref={(element) => { tabRefs.current[tab.id] = element; }}
          type="button"
          id={`${panelId}-tab-${tab.id}`}
          className={`sidebar-tab ${activeTab === tab.id ? 'active' : ''}`}
          role="tab"
          aria-selected={activeTab === tab.id}
          aria-controls={panelId}
          tabIndex={activeTab === tab.id ? 0 : -1}
          onClick={() => onActiveTabChange(tab.id)}
          onKeyDown={(event) => handleKeyDown(event, tab.id)}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );
}
