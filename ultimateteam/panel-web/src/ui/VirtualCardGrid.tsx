// A grid of drawn cards that renders only the rows near the screen, for piles of up to 2,000 cards. It scrolls with
// the page (window virtualiser), so the page keeps one scroll bar.
import { useWindowVirtualizer } from '@tanstack/react-virtual';
import { useLayoutEffect, useRef, useState, type ReactNode } from 'react';

const GAP = 12;

export function VirtualCardGrid<T>({ items, render, minWidth = 118 }: { items: T[]; render: (item: T) => ReactNode; minWidth?: number }) {
  const host = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);
  const [top, setTop] = useState(0);
  useLayoutEffect(() => {
    const node = host.current;
    if (!node) return undefined;
    const measure = () => { setWidth(node.clientWidth); setTop(node.getBoundingClientRect().top + window.scrollY); };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(node);
    return () => observer.disconnect();
  }, []);
  const columns = Math.max(1, Math.floor((width + GAP) / (minWidth + GAP)));
  const cardWidth = width ? (width - GAP * (columns - 1)) / columns : minWidth;
  const rowHeight = Math.round((cardWidth * 900) / 644) + GAP;
  const rows = Math.ceil(items.length / columns);
  const virtual = useWindowVirtualizer({ count: rows, estimateSize: () => rowHeight, overscan: 2, scrollMargin: top });
  // A new width changes every row's height.
  useLayoutEffect(() => { virtual.measure(); }, [rowHeight, virtual]);

  return (
    <div ref={host} style={{ position: 'relative', height: rows * rowHeight }}>
      {virtual.getVirtualItems().map((row) => (
        <div
          key={row.key}
          style={{
            position: 'absolute', left: 0, right: 0, top: row.start - top, height: rowHeight - GAP,
            display: 'grid', gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`, gap: GAP,
          }}
        >
          {items.slice(row.index * columns, row.index * columns + columns).map(render)}
        </div>
      ))}
    </div>
  );
}
