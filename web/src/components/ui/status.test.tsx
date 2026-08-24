import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { StatusPill, toneFor } from './status';

describe('the semantic status map', () => {
  it.each([
    ['Completed', 'success'],
    ['In Progress', 'info'],
    ['in-progress', 'info'],
    ['InProgress', 'info'],
    ['Scheduled', 'warning'],
    ['On Hold', 'neutral'],
    ['Overdue', 'danger'],
    ['Declined', 'danger'],
    ['High', 'danger'],
    ['Medium', 'warning'],
    ['Low', 'success'],
  ])('maps %s to the %s tone', (status, tone) => {
    expect(toneFor(status)).toBe(tone);
  });

  /** Failure path: a state machine GridCore has not seen yet must still render a readable pill. */
  it('falls back to neutral for a status it does not know', () => {
    expect(toneFor('Repatriated')).toBe('neutral');

    renderWithProviders(<StatusPill status="Repatriated" />);

    expect(screen.getByText('Repatriated')).toHaveClass('bg-neutral-soft');
  });

  it('lets a caller override the tone the map would pick', () => {
    renderWithProviders(<StatusPill status="Completed" tone="danger" />);

    expect(screen.getByText('Completed')).toHaveClass('bg-danger-soft');
  });
});
