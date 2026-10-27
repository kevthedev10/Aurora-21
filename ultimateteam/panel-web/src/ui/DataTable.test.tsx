import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { DataTable, type Column } from './DataTable';

interface Row { id: number; name: string; coins: number | null }
const rows: Row[] = [{ id: 1, name: 'b', coins: 5 }, { id: 2, name: 'a', coins: null }, { id: 3, name: 'c', coins: 50 }];
const columns: Column<Row>[] = [
  { key: 'name', header: 'Name', cell: (row) => row.name, sort: (row) => row.name },
  { key: 'coins', header: 'Coins', cell: (row) => String(row.coins ?? '–'), sort: (row) => row.coins, align: 'end' },
];
const names = () => screen.getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell')[0]?.textContent);

describe('DataTable', () => {
  it('sorts by a column, missing values last, and flips direction', () => {
    render(<DataTable columns={columns} rows={rows} rowKey={(row) => row.id} initialSort={{ key: 'coins', direction: 'desc' }} />);
    expect(names()).toEqual(['c', 'b', 'a']);
    fireEvent.click(screen.getByRole('button', { name: /Coins/ }));
    expect(names()).toEqual(['b', 'c', 'a']);
    expect(screen.getByRole('columnheader', { name: /Coins/ }).getAttribute('aria-sort')).toBe('ascending');
  });

  it('requests server sorting without reordering the loaded page', () => {
    const change = vi.fn();
    render(<DataTable columns={columns} rows={rows} rowKey={row => row.id}
      externalSort={{ key: 'coins', direction: 'desc' }} onSortChange={change} />);
    fireEvent.click(screen.getByRole('button', { name: 'Coins' }));
    expect(change).toHaveBeenCalledWith({ key: 'coins', direction: 'asc' });
    expect(names()).toEqual(['b', 'a', 'c']);
  });

  it('pages and opens rows by click or Enter', () => {
    const open = vi.fn();
    render(<DataTable columns={columns} rows={rows} rowKey={(row) => row.id} pageSize={2} onRowClick={open} rowLabel={(row) => `Open ${row.name}`} />);
    expect(screen.getByText('1–2 of 3')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(names()).toEqual(['c']);
    fireEvent.keyDown(screen.getByRole('row', { name: 'Open c' }), { key: 'Enter' });
    expect(open).toHaveBeenCalledWith(rows[2]);
  });

  it('shows the empty state for no rows', () => {
    render(<DataTable columns={columns} rows={[]} rowKey={(row) => row.id} empty={<p>Nothing here</p>} />);
    expect(screen.getByText('Nothing here')).toBeTruthy();
  });
});
