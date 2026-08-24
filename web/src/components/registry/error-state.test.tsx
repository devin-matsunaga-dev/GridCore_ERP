import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/api/client';
import { renderWithProviders } from '@/test/render';
import { ErrorState } from './error-state';

describe('ErrorState', () => {
  /**
   * The failure path this WP owes: a caller without `customers.read` gets a 403 from the RBAC gate,
   * and the screen has to say so. It is not an empty registry, and it is not offered a retry —
   * retrying a permission refusal just repeats it.
   */
  it('reads a 403 as a permission refusal with no retry', () => {
    const forbidden = new ApiError(
      403,
      { title: 'Forbidden', status: 403, detail: 'You do not hold customers.read.' },
      'failed',
    );

    renderWithProviders(<ErrorState error={forbidden} onRetry={vi.fn()} />);

    expect(screen.getByRole('alert')).toHaveTextContent('You do not have access to this');
    expect(screen.getByText('You do not hold customers.read.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument();
  });

  it('offers a retry for a server failure', async () => {
    const onRetry = vi.fn();
    renderWithProviders(
      <ErrorState error={new ApiError(500, { title: 'Server error', status: 500 }, 'failed')} onRetry={onRetry} />,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('That did not load');
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('explains an unreachable API rather than showing a raw message', () => {
    renderWithProviders(<ErrorState error={new ApiError(0, null, 'timed out')} onRetry={vi.fn()} />);

    expect(screen.getByRole('alert')).toHaveTextContent('GridCore is not responding');
  });

  it('falls back for a thrown value that is not an ApiError', () => {
    renderWithProviders(<ErrorState error={new Error('Boom')} />);

    expect(screen.getByRole('alert')).toHaveTextContent('Boom');
    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument();
  });
});
