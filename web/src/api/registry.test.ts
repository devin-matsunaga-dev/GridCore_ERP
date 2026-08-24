import { describe, expect, it } from 'vitest';
import { isWindowFull, registryWindow } from './registry';

describe('isWindowFull', () => {
  /**
   * The list endpoints report no total, so a full window is the only signal that rows may have been
   * cut off — and it is what the table's truncation notice hangs on.
   */
  it('is true only once the answer filled the window', () => {
    expect(isWindowFull(registryWindow)).toBe(true);
    expect(isWindowFull(registryWindow - 1)).toBe(false);
    expect(isWindowFull(0)).toBe(false);
  });

  /** Failure path: nothing has come back yet, which is not the same as a full window. */
  it('is false before an answer has arrived', () => {
    expect(isWindowFull(undefined)).toBe(false);
  });

  it('matches the services own MaxPageSize', () => {
    // `CustomerService.MaxPageSize` and its peers are 200; asking for more just gets clamped.
    expect(registryWindow).toBe(200);
  });
});
