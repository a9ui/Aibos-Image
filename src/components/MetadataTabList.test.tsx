import React, { useState } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MetadataTabList, type MetadataTab } from './MetadataTabList';

function MetadataTabsHarness() {
  const [activeTab, setActiveTab] = useState<MetadataTab>('prompt');
  return (
    <>
      <MetadataTabList
        activeTab={activeTab}
        onActiveTabChange={setActiveTab}
        panelId="metadata-panel"
      />
      <div id="metadata-panel" role="tabpanel" aria-labelledby={`metadata-panel-tab-${activeTab}`}>
        {activeTab} content
      </div>
    </>
  );
}

describe('MetadataTabList', () => {
  it('exposes the active metadata section through tab and tabpanel semantics', () => {
    render(<MetadataTabsHarness />);

    const tablist = screen.getByRole('tablist', { name: 'Image metadata' });
    const promptTab = screen.getByRole('tab', { name: 'Prompt' });
    const negativeTab = screen.getByRole('tab', { name: 'Negative' });
    const panel = screen.getByRole('tabpanel');

    expect(tablist).toContainElement(promptTab);
    expect(promptTab).toHaveAttribute('aria-selected', 'true');
    expect(promptTab).toHaveAttribute('tabindex', '0');
    expect(negativeTab).toHaveAttribute('aria-selected', 'false');
    expect(negativeTab).toHaveAttribute('tabindex', '-1');
    expect(panel).toHaveAttribute('aria-labelledby', 'metadata-panel-tab-prompt');
  });

  it('moves focus and active panel with Arrow keys, Home, and End', () => {
    render(<MetadataTabsHarness />);

    const promptTab = screen.getByRole('tab', { name: 'Prompt' });
    const negativeTab = screen.getByRole('tab', { name: 'Negative' });
    const settingsTab = screen.getByRole('tab', { name: 'Settings' });

    promptTab.focus();
    fireEvent.keyDown(promptTab, { key: 'ArrowRight' });
    expect(negativeTab).toHaveFocus();
    expect(negativeTab).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tabpanel')).toHaveTextContent('negative content');

    fireEvent.keyDown(negativeTab, { key: 'End' });
    expect(settingsTab).toHaveFocus();
    expect(settingsTab).toHaveAttribute('aria-selected', 'true');

    fireEvent.keyDown(settingsTab, { key: 'Home' });
    expect(promptTab).toHaveFocus();
    expect(promptTab).toHaveAttribute('aria-selected', 'true');

    fireEvent.keyDown(promptTab, { key: 'ArrowLeft' });
    expect(settingsTab).toHaveFocus();
    expect(settingsTab).toHaveAttribute('aria-selected', 'true');
  });
});
