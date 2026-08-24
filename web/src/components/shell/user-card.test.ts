import { describe, expect, it } from 'vitest';
import { initialsOf, primaryRoleTitle } from './user-card';

describe('initialsOf', () => {
  it.each([
    ['Jordan Smith', 'JS'],
    ['jordan smith', 'JS'],
    ['Maria de la Cruz', 'MC'],
    ['Jordan', 'JO'],
  ])('renders %s as %s', (name, expected) => {
    expect(initialsOf(name)).toBe(expected);
  });

  /** Failure path: a token with no usable name must not crash the sidebar. */
  it('falls back for an empty name', () => {
    expect(initialsOf('   ')).toBe('?');
  });
});

describe('primaryRoleTitle', () => {
  it('picks the highest-ranking role held', () => {
    expect(primaryRoleTitle(['Technician', 'Supervisor'])).toBe('Operations Supervisor');
  });

  it('describes a caller holding no GridCore role', () => {
    expect(primaryRoleTitle([])).toBe('GridCore user');
  });

  /** Roles the realm carries for other systems must not become a job title. */
  it('ignores roles GridCore does not define', () => {
    expect(primaryRoleTitle(['offline_access', 'default-roles-gridcore'])).toBe('GridCore user');
  });
});
