export function Tradeability({ value, onChange, forced = false, forcedReason = 'Developer Cards are always untradeable.', disabled = false }: { value: boolean; onChange: (value: boolean) => void; forced?: boolean; forcedReason?: string; disabled?: boolean }) {
  return <div className="tradeability-field"><label className="tradeability-check"><input type="checkbox" checked={forced || value} disabled={forced || disabled} onChange={e => { if (!forced && !disabled) onChange(e.target.checked); }} />Untradeable</label>
    <p className="dim tradeability-hint">{forced ? forcedReason : 'Leave unchecked for tradeable items.'}</p></div>;
}
