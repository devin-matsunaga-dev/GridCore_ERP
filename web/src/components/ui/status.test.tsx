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

describe('the registry lifecycles (WP-1.5)', () => {
  /**
   * DESIGN.md: every state machine renders as a pill through this one map. A status with no entry
   * would fall back to neutral and quietly lose its meaning, so each lifecycle is asserted here.
   */
  it('gives the customer lifecycle its tones', () => {
    expect(toneFor('Prospect')).toBe('info');
    expect(toneFor('Active')).toBe('success');
    expect(toneFor('Suspended')).toBe('warning');
    expect(toneFor('Closed')).toBe('neutral');
  });

  it('gives the service-account lifecycle its tones', () => {
    expect(toneFor('Pending')).toBe('warning');
    expect(toneFor('Disconnected')).toBe('danger');
  });

  it('gives the asset lifecycle its tones, including the PascalCase names', () => {
    expect(toneFor('InStorage')).toBe('neutral');
    expect(toneFor('InService')).toBe('success');
    expect(toneFor('UnderMaintenance')).toBe('warning');
    expect(toneFor('Retired')).toBe('neutral');
  });

  it('grades an asset condition, worst two both reading as something to act on', () => {
    expect(toneFor('Excellent')).toBe('success');
    expect(toneFor('Good')).toBe('success');
    expect(toneFor('Fair')).toBe('warning');
    expect(toneFor('Poor')).toBe('danger');
    expect(toneFor('Critical')).toBe('danger');
    expect(toneFor('Unknown')).toBe('neutral');
  });

  it('tones the stock statuses and the three movement types', () => {
    expect(toneFor('In stock')).toBe('success');
    expect(toneFor('Low stock')).toBe('danger');
    expect(toneFor('Discontinued')).toBe('neutral');
    expect(toneFor('Receipt')).toBe('success');
    expect(toneFor('Issue')).toBe('info');
    expect(toneFor('Adjustment')).toBe('warning');
  });

  /**
   * Failure path, and why the low-stock pill is not labelled "Low": the priority scale already
   * claims that word for a *good* thing, so the two must stay distinguishable.
   */
  it('keeps the low-stock pill distinct from a low priority', () => {
    expect(toneFor('Low')).toBe('success');
    expect(toneFor('Low stock')).toBe('danger');
  });

  it('tones the meter lifecycle (WP-2.1)', () => {
    expect(toneFor('In store')).toBe('neutral');
    expect(toneFor('Installed')).toBe('success');
    expect(toneFor('Faulty')).toBe('danger');
    expect(toneFor('Removed')).toBe('neutral');
    expect(toneFor('Retired')).toBe('neutral');
  });

  /**
   * Failure path of the same family as the low-stock one, and why a meter is "In store" rather than
   * "In stock": Inventory already uses that phrase for a catalogue line that is stocked and above
   * its reorder level, which is a good thing. A meter sitting in a store is neither good nor bad,
   * and one key cannot mean both — the map is shared by every screen in the app.
   */
  it('keeps a meter in a store distinct from a catalogue line being in stock', () => {
    expect(toneFor('In stock')).toBe('success');
    expect(toneFor('In store')).toBe('neutral');
    expect(toneFor('In stock')).not.toBe(toneFor('In store'));
  });
});
