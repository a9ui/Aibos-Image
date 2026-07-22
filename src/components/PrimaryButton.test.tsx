import React from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { PrimaryButton } from './PrimaryButton';

describe('PrimaryButton', () => {
  it('renders an external link when href points off-site', () => {
    render(<PrimaryButton label="Docs" href="https://nextjs.org/docs" />);

    const link = screen.getByRole('link', { name: 'Docs' });
    expect(link).toHaveAttribute('href', 'https://nextjs.org/docs');
    expect(link).toHaveAttribute('target', '_blank');
  });

  it('falls back to a button and emits click events', async () => {
    const user = userEvent.setup();
    const handleClick = vi.fn();

    render(<PrimaryButton label="Click me" onClick={handleClick} />);

    await user.click(screen.getByRole('button', { name: 'Click me' }));
    expect(handleClick).toHaveBeenCalledTimes(1);
  });
});
