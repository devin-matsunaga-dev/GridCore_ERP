import { describe, expect, it } from 'vitest';
import { fallbackApiTarget, resolveApiTarget } from './api-target';

describe('resolveApiTarget', () => {
  it('uses the Aspire service-discovery variable for the host', () => {
    const { target, source } = resolveApiTarget({ services__web_host__http__0: undefined, 'services__web-host__http__0': 'http://localhost:5273' });

    expect(target).toBe('http://localhost:5273');
    expect(source).toBe('services__web-host__http__0');
  });

  it('prefers https when the host offers both', () => {
    const { target } = resolveApiTarget({
      'services__web-host__http__0': 'http://localhost:5273',
      'services__web-host__https__0': 'https://localhost:7036',
    });

    expect(target).toBe('https://localhost:7036');
  });

  it('ignores service-discovery variables for other resources', () => {
    const { target } = resolveApiTarget({ 'services__keycloak__http__0': 'http://localhost:8080' });

    expect(target).toBe(fallbackApiTarget);
  });

  it('honours an explicit override when Aspire is not in play', () => {
    const { target, source } = resolveApiTarget({ VITE_API_URL: 'http://localhost:9999' });

    expect(target).toBe('http://localhost:9999');
    expect(source).toBe('VITE_API_URL');
  });

  /**
   * Failure path — and the one that matters: running `npm run dev` outside the AppHost must land
   * on the Web.Host launch profile, not on a port nothing is listening to.
   */
  it('falls back to the Web.Host launch profile and says so', () => {
    const { target, source } = resolveApiTarget({});

    expect(target).toBe(fallbackApiTarget);
    expect(source).toContain('no Aspire service discovery variables');
  });

  it('skips a variable that is present but empty', () => {
    const { target } = resolveApiTarget({ 'services__web-host__http__0': '' });

    expect(target).toBe(fallbackApiTarget);
  });
});
