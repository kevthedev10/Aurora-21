import { describe, expect, it } from 'vitest';
import { ApiError } from './client';
import { describeEdit, refusedUnchanged, supports } from './players';

describe('club edits', () => {
  it('describes each stored edit in words', () => {
    expect(describeEdit('{"coinsDelta":5000,"pointsDelta":-100}')).toBe('Coins +5,000, Aurora Tokens −100');
    expect(describeEdit('{"coins":1234}')).toBe('Coins set to 1,234');
    expect(describeEdit('{"op":"grantSeasonXp","amount":1500}')).toBe('Granted 1,500 Season XP');
    expect(describeEdit('{"op":"grantPacks","packId":3,"count":2}', () => 'Gold Pack')).toBe('Gifted 2 × Gold Pack');
    expect(describeEdit('{"op":"grantCard","card":"158023:93:1","count":1}')).toBe('Granted card 158023:93:1');
    expect(describeEdit('{"op":"grantItem","item":"position_modifier:5001","count":3}')).toBe('Granted item position_modifier:5001 × 3');
    expect(describeEdit('{"op":"access","blocked":true,"maintenanceBypass":false,"note":"spam"}')).toBe('Access: blocked, maintenance bypass off, note “spam”');
    expect(describeEdit('{"op":"resetMode","mode":"squad-battles"}')).toBe('Reset Squad Battles progress');
    expect(describeEdit('not json')).toBe('not json');
  });

  it('offers only the edits the server applies', () => {
    const detail = { id: 1, editOps: { ops: ['grantPacks', 'grantCard', 'access'], deltas: true } };
    expect(supports(detail, 'deltas')).toBe(true);
    expect(supports(detail, 'grantPacks')).toBe(true);
    expect(supports(detail, 'dedupeCards')).toBe(false);
    expect(supports({ id: 1 }, 'dedupeCards')).toBe(true);
  });

  it('keeps the form only after a refusal that changed nothing', () => {
    expect(refusedUnchanged(new ApiError('bad', 400, null))).toBe(true);
    expect(refusedUnchanged(new ApiError('boom', 500, null))).toBe(false);
    expect(refusedUnchanged(new TypeError('Failed to fetch'))).toBe(false);
  });
});
