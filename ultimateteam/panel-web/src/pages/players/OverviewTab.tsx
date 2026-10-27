// Club overview (plan 4.4): set the balances, or add and take away. Both apply at once to the live club.
import { useState, type FormEvent } from 'react';
import { refusedUnchanged, supports, useClubEdit, type PlayerDetail } from '../../api/players';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { ConfirmDialog } from '../../ui/Dialog';
import { num } from '../../ui/format';
import { Section } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';

const MAX = 99_999_999;
const WHOLE = /^[+-]?\d{1,8}$/;

export function OverviewTab({ detail }: { detail: PlayerDetail }) {
  if (!detail.club) return null;
  return (
    <div className="two-columns club-section-grid">
      {/* Keyed by the balances, so the form shows new values after any edit. */}
      <SetBalances key={`${detail.club.coins}-${detail.club.points}`} detail={detail} />
      <AddOrTakeAway detail={detail} />
      <GrantSeasonXp detail={detail} />
      <ClubContents detail={detail} />
      <ResetClub detail={detail} />
    </div>
  );
}

function ResetClub({ detail }: { detail: PlayerDetail }) {
  const [open, setOpen] = useState(false);
  const edit = useClubEdit(detail.id);
  const toast = useToast();
  return <Section title="Reset club">
    <p>Delete all club items, packs, coins, Aurora Tokens, squads and progress. The player must complete onboarding again.</p>
    {detail.online && <p className="notice">The player must leave FIFA 21 before resetting their club.</p>}
    <Button variant="danger" disabled={detail.online || edit.isPending || !supports(detail, 'resetClub')} onClick={() => setOpen(true)}>Reset club</Button>
    <ConfirmDialog open={open} onOpenChange={setOpen} title={`Reset club for ${detail.name ?? `persona ${detail.id}`}?`}
      text={`This permanently deletes the club contents and progress for persona ${detail.id}. Their player access and audit history stay. Next time they enter Ultimate Team, onboarding starts again.`}
      confirmLabel="Delete club contents" variant="danger" onConfirm={async () => {
        await edit.mutateAsync({ op: 'resetClub', confirm: true, confirmPersonaId: detail.id });
        toast('Club reset. The player must complete onboarding again.');
      }} />
  </Section>;
}

function SetBalances({ detail }: { detail: PlayerDetail }) {
  const toast = useToast();
  const edit = useClubEdit(detail.id);
  const [coins, setCoins] = useState(String(detail.club?.coins ?? ''));
  const [points, setPoints] = useState(String(detail.club?.points ?? ''));
  const [error, setError] = useState<string | null>(null);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    const patch: Record<string, number> = {};
    for (const [key, text, current, label] of [['coins', coins, detail.club?.coins, 'Coins'], ['points', points, detail.club?.points, 'Aurora Tokens']] as const) {
      const value = Number(text.trim());
      if (!/^\d{1,8}$/.test(text.trim()) || value > MAX) { setError(`${label}: a whole number from 0 to 99,999,999.`); return; }
      if (value !== current) patch[key] = value;
    }
    if (!Object.keys(patch).length) { setError('Nothing changed.'); return; }
    edit.mutate(patch, {
      onSuccess: (next) => {
        setCoins(String(next.club?.coins ?? '')); setPoints(String(next.club?.points ?? ''));
        toast(`Balances set for ${detail.name ?? 'the club'}.`);
      },
      onError: (caught) => setError(caught.message),
    });
  };
  return (
    <Section className="club-balance-panel" title="Set balances" text="Replace the current coin and Aurora Token balances. Changes apply immediately.">
      <form className="stack" onSubmit={submit}>
        <div className="form-row">
          <Field label="Coins" hint={`now ${num(detail.club?.coins)}`}><input inputMode="numeric" value={coins} onChange={(event) => setCoins(event.target.value)} /></Field>
          <Field label="Aurora Tokens" hint={`now ${num(detail.club?.points)}`}><input inputMode="numeric" value={points} onChange={(event) => setPoints(event.target.value)} /></Field>
        </div>
        {error && <p className="error" role="alert">{error}</p>}
        <div><Button variant="primary" type="submit" disabled={edit.isPending}>Apply</Button></div>
      </form>
    </Section>
  );
}

function AddOrTakeAway({ detail }: { detail: PlayerDetail }) {
  const toast = useToast();
  const edit = useClubEdit(detail.id);
  const [coins, setCoins] = useState('');
  const [points, setPoints] = useState('');
  const [error, setError] = useState<string | null>(null);
  const available = supports(detail, 'deltas');
  const submit = (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    const patch: Record<string, number> = {};
    for (const [key, text, label] of [['coinsDelta', coins, 'Coins'], ['pointsDelta', points, 'Aurora Tokens']] as const) {
      const trimmed = text.trim();
      if (!trimmed) continue;
      if (!WHOLE.test(trimmed) || Number(trimmed) === 0) { setError(`${label}: a whole number from −99,999,999 to 99,999,999, not 0.`); return; }
      patch[key] = Number(trimmed);
    }
    if (!Object.keys(patch).length) { setError('Enter an amount to add, or a negative one to take away.'); return; }
    edit.mutate(patch, {
      onSuccess: () => { setCoins(''); setPoints(''); toast(`Applied to ${detail.name ?? 'the club'}.`); },
      onError: (caught) => {
        // A refusal keeps the amounts; after anything else the change may have applied, so they clear.
        if (!refusedUnchanged(caught)) { setCoins(''); setPoints(''); }
        setError(caught.message);
      },
    });
  };
  return (
    <Section className="club-balance-panel" title="Add or take away" text="Add positive amounts or subtract negative amounts from the current balances.">
      <form className="stack" onSubmit={submit}>
        <div className="form-row">
          <Field label="Coins +/−" hint="e.g. 5000 or -1000"><input inputMode="numeric" value={coins} onChange={(event) => setCoins(event.target.value)} autoComplete="off" /></Field>
          <Field label="Aurora Tokens +/−" hint="e.g. 500 or -100"><input inputMode="numeric" value={points} onChange={(event) => setPoints(event.target.value)} autoComplete="off" /></Field>
        </div>
        {!available && <p className="error">This server does not apply balance changes.</p>}
        {error && <p className="error" role="alert">{error}</p>}
        <div><Button variant="primary" type="submit" disabled={edit.isPending || !available}>Apply</Button></div>
      </form>
    </Section>
  );
}

function GrantSeasonXp({ detail }: { detail: PlayerDetail }) {
  const toast = useToast();
  const edit = useClubEdit(detail.id);
  const [amount, setAmount] = useState('');
  const [error, setError] = useState<string | null>(null);
  const available = supports(detail, 'grantSeasonXp');
  const submit = (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    const value = Number(amount.trim());
    if (!/^\d{1,8}$/.test(amount.trim()) || value < 1 || value > MAX) {
      setError('Enter a whole number from 1 to 99,999,999.');
      return;
    }
    edit.mutate({ op: 'grantSeasonXp', amount: value }, {
      onSuccess: () => { setAmount(''); toast(`Granted ${num(value)} Season XP to ${detail.name ?? 'the club'}.`); },
      onError: (caught) => {
        if (!refusedUnchanged(caught)) setAmount('');
        setError(caught.message);
      },
    });
  };
  return (
    <Section className="club-balance-panel" title="Season Progress XP" text="Add XP immediately. Level rewards remain available for the player to claim in FIFA.">
      <form className="stack" onSubmit={submit}>
        <p>Current Season XP: <strong>{num(detail.club?.seasonXp)}</strong></p>
        <Field label="XP to grant"><input inputMode="numeric" autoComplete="off" value={amount} onChange={(event) => setAmount(event.target.value)} disabled={edit.isPending || !available} /></Field>
        {!available && <p className="error">This server does not support Season XP grants.</p>}
        {error && <p className="error" role="alert">{error}</p>}
        <div><Button variant="primary" type="submit" disabled={edit.isPending || !available}>Grant Season XP</Button></div>
      </form>
    </Section>
  );
}

function ClubContents({ detail }: { detail: PlayerDetail }) {
  const kinds = detail.inventory?.byKind;
  if (!kinds) return null;
  const rows: [string, number | undefined][] = [
    ['Players', kinds.player], ['Managers', kinds.manager], ['Consumables', kinds.consumable], ['Club items', kinds.clubItem], ['Other', kinds.other],
  ];
  return (
    <Section title="What the club holds" text="Every item the club owns, in any pile.">
      <dl className="facts">
        {rows.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{num(value ?? 0)}</dd></div>)}
        <div><dt>Total</dt><dd>{num(detail.inventory?.total)}</dd></div>
      </dl>
    </Section>
  );
}
