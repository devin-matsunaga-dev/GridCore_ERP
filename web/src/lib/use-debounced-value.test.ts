import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useDebouncedValue } from './use-debounced-value';

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe('useDebouncedValue', () => {
  it('holds the new value back until the delay has passed', () => {
    const { result, rerender } = renderHook(({ value }) => useDebouncedValue(value, 300), {
      initialProps: { value: 'S' },
    });

    rerender({ value: 'So' });
    expect(result.current).toBe('S');

    act(() => void vi.advanceTimersByTime(299));
    expect(result.current).toBe('S');

    act(() => void vi.advanceTimersByTime(1));
    expect(result.current).toBe('So');
  });

  /**
   * The point of the hook: a search that runs on the server must not be one request per keystroke,
   * and the answers must not land out of order.
   */
  it('reports only the last value of a burst', () => {
    const { result, rerender } = renderHook(({ value }) => useDebouncedValue(value, 300), {
      initialProps: { value: '' },
    });

    for (const value of ['S', 'So', 'Son', 'Song']) {
      rerender({ value });
      act(() => void vi.advanceTimersByTime(100));
    }

    // 300ms of typing, but no gap of 300ms — nothing has settled yet.
    expect(result.current).toBe('');

    act(() => void vi.advanceTimersByTime(300));
    expect(result.current).toBe('Song');
  });

  /** Clearing the box is not a search: the whole registry comes back without waiting out the timer. */
  it('reports an empty value immediately', () => {
    const { result, rerender } = renderHook(({ value }) => useDebouncedValue(value, 300), {
      initialProps: { value: 'Songsong' },
    });

    act(() => void vi.advanceTimersByTime(300));
    expect(result.current).toBe('Songsong');

    rerender({ value: '' });
    expect(result.current).toBe('');
  });
});
