import styles from './StatBar.module.css';

/** FUT's stat colour scale: green from 80, light green 70s, yellow 60s, orange 50s, red below. */
export const statColour = (value: number) =>
  value >= 80 ? '#4cc76c' : value >= 70 ? '#a3cf4c' : value >= 60 ? '#e2c046' : value >= 50 ? '#e9903c' : '#e5534b';

export function StatBar({ label, value }: { label: string; value: number }) {
  const colour = statColour(value);
  return (
    <div className={styles.row}>
      <span>{label}</span>
      <span className={styles.value} style={{ color: colour }}>{value}</span>
      <span className={styles.track} aria-hidden="true"><span className={styles.fill} style={{ display: 'block', width: `${Math.max(0, Math.min(99, value))}%`, background: colour }} /></span>
    </div>
  );
}

/** A face stat with its detailed stats beneath (Pace: Acceleration, Sprint Speed). */
export function StatGroup({ label, value, stats }: { label: string; value: number; stats: { label: string; value: number }[] }) {
  return (
    <div className={styles.group}>
      <div className={styles.groupHead}><span>{label}</span><span style={{ color: statColour(value) }}>{value}</span></div>
      {stats.map((stat) => <StatBar key={stat.label} {...stat} />)}
    </div>
  );
}
