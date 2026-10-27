import { afterEach, describe, expect, it } from 'vitest';
import { getContentDestination, setContentDestination } from './client';
import { recoveryKey, recoveryStorageKey, rememberRecovery } from './content';

afterEach(() => { setContentDestination(null); sessionStorage.clear(); });
describe('content destination recovery', () => {
  it('retains interrupted edits separately for immediate changes and each drop', () => {
    const change = { id: 1, area: 'promo', key: { code: 'halloween' }, operation: 'update', after: { name: 'Halloween' } };
    setContentDestination(null);
    rememberRecovery(change);
    expect(recoveryStorageKey()).toBe(recoveryKey);
    setContentDestination(7);
    expect(recoveryStorageKey()).toBe(`${recoveryKey}.drop.7`);
    expect(sessionStorage.getItem(recoveryStorageKey())).toBeNull();
    rememberRecovery({ ...change, id: 2 });
    setContentDestination(8);
    expect(sessionStorage.getItem(recoveryStorageKey())).toBeNull();
    setContentDestination(7);
    expect(JSON.parse(sessionStorage.getItem(recoveryStorageKey())!)).toMatchObject({ id: 2 });
    rememberRecovery(null);
    setContentDestination(null);
    expect(JSON.parse(sessionStorage.getItem(recoveryStorageKey())!)).toMatchObject({ id: 1 });
    expect(getContentDestination()).toBeNull();
  });
});
