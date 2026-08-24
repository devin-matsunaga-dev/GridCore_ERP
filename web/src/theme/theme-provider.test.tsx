import { act, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ThemeProvider, useTheme } from './theme-provider';

function renderTheme() {
  return renderHook(() => useTheme(), { wrapper: ThemeProvider });
}

afterEach(() => {
  localStorage.clear();
  document.documentElement.classList.remove('dark');
});

describe('ThemeProvider', () => {
  it('defaults to the system preference, resolved to light in this environment', () => {
    const { result } = renderTheme();

    expect(result.current.theme).toBe('system');
    expect(result.current.resolvedTheme).toBe('light');
    expect(document.documentElement).not.toHaveClass('dark');
  });

  it('paints and persists an explicit dark choice', () => {
    const { result } = renderTheme();

    act(() => result.current.setTheme('dark'));

    expect(result.current.resolvedTheme).toBe('dark');
    expect(document.documentElement).toHaveClass('dark');
    expect(localStorage.getItem('gridcore.theme')).toBe('dark');
  });

  it('restores the stored choice on the next visit', () => {
    localStorage.setItem('gridcore.theme', 'dark');

    const { result } = renderTheme();

    expect(result.current.theme).toBe('dark');
    expect(document.documentElement).toHaveClass('dark');
  });

  /** Failure path: a private window throws on storage access; the app must still render. */
  it('falls back to system when storage is unreadable', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('The operation is insecure.');
    });

    const { result } = renderTheme();

    expect(result.current.theme).toBe('system');
  });

  it('refuses to run outside the provider', () => {
    expect(() => renderHook(() => useTheme())).toThrow(/inside <ThemeProvider>/);
  });
});
