import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Select } from './select';

describe('Select', () => {
  it('shrinks to its options by default, which is what a filter or a card header wants', () => {
    render(<Select aria-label="Period" options={['This Year', 'Last Year']} />);

    const wrapper = screen.getByLabelText('Period').parentElement;

    expect(wrapper).toHaveClass('inline-flex');
    expect(wrapper).not.toHaveClass('w-full');
  });

  /**
   * The wrapper is what the chevron is positioned against, so it — not the `<select>` — decides the
   * control's width. Left `inline-flex`, a `w-full` on the select resolves against a parent that
   * has already shrunk to the select's own content, and a form field that should line up with the
   * inputs beside it renders as wide as its longest option.
   */
  it('fills the field when asked, so a form control lines up with the inputs beside it', () => {
    render(<Select aria-label="Class" fullWidth className="w-full" options={['Residential']} />);

    const wrapper = screen.getByLabelText('Class').parentElement;

    expect(wrapper).toHaveClass('w-full');
    expect(wrapper).not.toHaveClass('inline-flex');
  });

  it('keeps the chevron positioned in both modes', () => {
    const { rerender } = render(<Select aria-label="Class" options={['Residential']} />);

    expect(screen.getByLabelText('Class').parentElement).toHaveClass('relative');

    rerender(<Select aria-label="Class" fullWidth options={['Residential']} />);

    expect(screen.getByLabelText('Class').parentElement).toHaveClass('relative');
  });
});
