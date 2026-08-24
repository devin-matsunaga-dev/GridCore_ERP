import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { Sparkline } from './sparkline';

function pointsOf(container: HTMLElement): [number, number][] {
  const raw = container.querySelector('polyline')?.getAttribute('points') ?? '';

  return raw
    .split(' ')
    .filter(Boolean)
    .map((pair) => pair.split(',').map(Number) as [number, number]);
}

describe('Sparkline', () => {
  it('plots one point per value, left to right', () => {
    const { container } = renderWithProviders(<Sparkline values={[1, 2, 3, 4]} />);
    const points = pointsOf(container);

    expect(points).toHaveLength(4);
    expect(points.map(([x]) => x)).toEqual([...points.map(([x]) => x)].sort((a, b) => a - b));
  });

  it('draws a rising series upwards — smaller y is higher on screen', () => {
    const { container } = renderWithProviders(<Sparkline values={[1, 5]} />);
    const [first, last] = pointsOf(container);

    expect(last![1]).toBeLessThan(first![1]);
  });

  /** Failure path: a flat series would divide by a zero range. */
  it('draws a flat series without producing NaN coordinates', () => {
    const { container } = renderWithProviders(<Sparkline values={[7, 7, 7]} />);

    for (const [x, y] of pointsOf(container)) {
      expect(Number.isFinite(x)).toBe(true);
      expect(Number.isFinite(y)).toBe(true);
    }
  });

  /** Failure path: one point is not a line; render nothing rather than a broken polyline. */
  it('renders nothing for a series too short to draw', () => {
    const { container } = renderWithProviders(<Sparkline values={[1]} />);

    expect(container.querySelector('svg')).toBeNull();
  });
});
