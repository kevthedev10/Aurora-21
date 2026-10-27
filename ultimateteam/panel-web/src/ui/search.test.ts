import { expect, it } from 'vitest';
import { searchText } from './search';

it('matches accented and decomposed names without changing punctuation or IDs', () => {
  expect(searchText('EUSÉBIO')).toBe(searchText('Eusebio'));
  expect(searchText('Euse\u0301bio')).toBe(searchText('Eusébio'));
  expect(searchText('João Félix')).toContain(searchText('Joao Felix'));
  expect(searchText('123:84:3')).toBe('123:84:3');
});
