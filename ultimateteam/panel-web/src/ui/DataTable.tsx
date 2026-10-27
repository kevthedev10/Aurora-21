import { useMemo, useState, type KeyboardEvent, type ReactNode } from 'react';
import { Button } from './Button';
import { cx } from './cx';
import { EmptyState } from './Layout';
import styles from './DataTable.module.css';

export interface Column<T> {
  key: string;
  /** The header text; also the row label on phones. */
  header: string;
  cell: (row: T) => ReactNode;
  /** Makes the column sortable by this value. */
  sort?: (row: T) => string | number | null | undefined;
  /** Header sorting supplied by onSortChange for a server-paged table. */
  sortable?: boolean;
  width?: number;
  align?: 'start' | 'center' | 'end';
}

type Direction = 'asc' | 'desc';

type Sortable = string | number | null | undefined;
const missing = (value: Sortable) => value == null || value === '';

/** Orders two values in the given direction; a missing value always sorts last, whichever the direction. */
const compare = (a: Sortable, b: Sortable, factor: number) => {
  if (missing(a)) return missing(b) ? 0 : 1;
  if (missing(b)) return -1;
  const order = typeof a === 'number' && typeof b === 'number' ? a - b : String(a).localeCompare(String(b), 'en', { numeric: true });
  return order * factor;
};

/**
 * A table of rows already in memory: optional sorting per column, paging, and a click (or Enter) per row. Server-paged
 * lists pass one page of rows and their own pager in `footer`.
 */
export function DataTable<T>({ columns, rows, rowKey, onRowClick, rowLabel, empty, pageSize, initialSort, externalSort, onSortChange, footer, caption, fixedLayout = false }: {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string | number;
  onRowClick?: (row: T) => void;
  /** The accessible name of a clickable row ("Open Messi"). */
  rowLabel?: (row: T) => string;
  empty?: ReactNode;
  pageSize?: number;
  initialSort?: { key: string; direction: Direction };
  externalSort?: { key: string; direction: Direction };
  /** Request a server-ordered page rather than sorting only the loaded rows. */
  onSortChange?: (sort: { key: string; direction: Direction }) => void;
  footer?: ReactNode;
  caption?: string;
  fixedLayout?: boolean;
}) {
  const [localSort, setSort] = useState(initialSort ?? null);
  const sort = onSortChange ? externalSort ?? null : localSort;
  const [page, setPage] = useState(0);
  const sorted = useMemo(() => {
    const column = sort && columns.find((candidate) => candidate.key === sort.key);
    if (onSortChange || !column?.sort || !sort) return rows;
    const by = column.sort;
    const factor = sort.direction === 'asc' ? 1 : -1;
    return [...rows].sort((a, b) => compare(by(a), by(b), factor));
  }, [rows, columns, sort, onSortChange]);
  const pages = pageSize ? Math.max(1, Math.ceil(sorted.length / pageSize)) : 1;
  const current = Math.min(page, pages - 1);
  const shown = pageSize ? sorted.slice(current * pageSize, (current + 1) * pageSize) : sorted;

  if (!rows.length) return <>{empty ?? <EmptyState title="Nothing to show" />}</>;

  const toggle = (key: string) => {
    setPage(0);
    const next: { key: string; direction: Direction } = { key, direction: sort?.key === key && sort.direction === 'desc' ? 'asc' : 'desc' };
    if (onSortChange) onSortChange(next); else setSort(next);
  };
  const onKey = (event: KeyboardEvent, row: T) => {
    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onRowClick?.(row); }
  };

  return (
    <div>
      <div className={styles.wrap}>
        <table className={cx(styles.table, fixedLayout && styles.fixed)} style={fixedLayout ? { '--table-min-width': `${columns.reduce((total, column) => total + (column.width ?? 0), 0)}px` } as React.CSSProperties : undefined}>
          {caption && <caption className="visually-hidden">{caption}</caption>}
          {fixedLayout && <colgroup>{columns.map(column => <col key={column.key} style={{ width: column.width }} />)}</colgroup>}
          <thead>
            <tr>
              {columns.map((column) => {
                const active = sort?.key === column.key;
                return (
                  <th key={column.key} className={cx(column.align === 'end' && styles.end, column.align === 'center' && styles.center)} aria-sort={active ? (sort.direction === 'asc' ? 'ascending' : 'descending') : undefined} scope="col">
                    {column.sort || (column.sortable && onSortChange) ? (
                      <button type="button" className={styles.sort} onClick={() => toggle(column.key)}>
                        {column.header}
                        <span className={styles.arrow} aria-hidden="true">{active ? (sort.direction === 'asc' ? '▲' : '▼') : ''}</span>
                      </button>
                    ) : column.header}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {shown.map((row) => (
              <tr
                key={rowKey(row)}
                className={cx(onRowClick && styles.clickable)}
                {...(onRowClick && { tabIndex: 0, onClick: () => onRowClick(row), onKeyDown: (event: KeyboardEvent) => onKey(event, row), 'aria-label': rowLabel?.(row) })}
              >
                {columns.map((column) => (
                  <td key={column.key} data-label={column.header} className={cx(column.align === 'end' && styles.end, column.align === 'center' && styles.center)}>{column.cell(row)}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {(pages > 1 || footer) && (
        <div className={styles.pager}>
          {pages > 1 ? (
            <>
              <span>{`${(current * (pageSize ?? 0) + 1).toLocaleString('en-GB')}–${Math.min(sorted.length, (current + 1) * (pageSize ?? 0)).toLocaleString('en-GB')} of ${sorted.length.toLocaleString('en-GB')}`}</span>
              <span className={styles.pagerButtons}>
                <Button size="sm" onClick={() => setPage(current - 1)} disabled={current === 0}>Previous</Button>
                <Button size="sm" onClick={() => setPage(current + 1)} disabled={current >= pages - 1}>Next</Button>
              </span>
            </>
          ) : <span />}
          {footer}
        </div>
      )}
    </div>
  );
}
