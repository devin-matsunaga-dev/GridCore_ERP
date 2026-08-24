import { describe, expect, it } from 'vitest';
import { ApiError } from '@/api/client';
import { describeError } from './toast';

describe('describeError', () => {
  it('explains a 403 as a permission problem', () => {
    expect(describeError(new ApiError(403, null, 'fallback'))).toBe('You do not have permission to do that.');
  });

  it('prefers the host detail when it sent one', () => {
    const error = new ApiError(403, { detail: 'Approvals require platform.approve.' }, 'fallback');

    expect(describeError(error)).toBe('Approvals require platform.approve.');
  });

  it('explains a 401 as an expired session', () => {
    expect(describeError(new ApiError(401, null, 'fallback'))).toContain('session has expired');
  });

  it('explains a 409 as a workflow conflict', () => {
    expect(describeError(new ApiError(409, null, 'fallback'))).toContain('conflicts with the current state');
  });

  /** Failure path: a thrown non-Error (a rejected promise carrying a string) must still read. */
  it('falls back for a value that is not an Error at all', () => {
    expect(describeError('boom', 'Could not save.')).toBe('Could not save.');
  });
});
