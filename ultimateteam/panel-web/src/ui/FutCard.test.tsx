import { describe, expect, it } from 'vitest';
import { fireEvent, render } from '@testing-library/react';
import { band, cardLook, frameFor, FutCard } from './FutCard';

const FRAMES = ['0_bronze.png', '0_gold.png', '0_silver.png', '1_gold.png', '3_bronze.png', '3_gold.png', '3_silver.png', '12_icon.png', '6_rb.png'];

describe('card art', () => {
  it('uses an explicitly selected base face without falling back to the old Pro portrait', () => {
    const { container } = render(<FutCard art={{ resourceId: 167961228, assetId: 189068,
      headResourceId: 189068, portraitId: 189068, defaultPortraitUrl: '/panel/assets/dynamic/p167961228.png' }} assets={{ frames: [] }} />);
    expect(container.querySelector('img')!.getAttribute('src')).toBe('/panel/assets/players/189068.png');
    fireEvent.error(container.querySelector('img')!);
    expect(container.querySelector('img')).toBeNull();
  });
  it('falls back to the base face if an explicitly selected dynamic image is missing', () => {
    const { container } = render(<FutCard art={{ resourceId: 167961228, assetId: 189068,
      headResourceId: 167984358, portraitId: 167984358 }} assets={{ frames: [] }} />);
    expect(container.querySelector('img')!.getAttribute('src')).toBe('/panel/assets/dynamic/p167984358.png');
    fireEvent.error(container.querySelector('img')!);
    expect(container.querySelector('img')!.getAttribute('src')).toBe('/panel/assets/players/189068.png');
  });
  it('prefers the native portrait preview over an inherited dynamic reference and shows card details', () => {
    const page = render(<FutCard size="catalogue" art={{ resourceId: 167984358, assetId: 212198, rating: 99, rareflag: 10,
      portraitId: 201538790, defaultPortraitUrl: '/panel/assets/dynamic/p167984358.png', skillMoves: 5, weakFoot: 4,
      workRateAttack: 'High', workRateDefense: 'Medium' }} assets={{ frames: [] }} />);
    expect(page.container.querySelector('img')!.getAttribute('src')).toBe('/panel/assets/dynamic/p167984358.png');
    expect(page.getByTitle('Skill moves').textContent).toBe('5★');
    expect(page.getByTitle('Weak foot').textContent).toBe('4★');
    expect(page.getByTitle('Work rate: attacking / defending').textContent).toBe('H/M');
  });
  it('uses only the frame and portrait in table thumbnails, retaining dynamic-image fallback', () => {
    const { container } = render(<FutCard size="thumbnail" art={{ resourceId: 117598535, assetId: 158023, rareflag: 5, rating: 98, name: 'Messi', position: 'RW' }} assets={{ frames: ['5_toty.png'] }} />);
    expect(container.textContent).toBe('');
    expect([...container.querySelectorAll('img')].map(img => img.getAttribute('src'))).toEqual(['/panel/assets/cards/hd/5_toty.png', '/panel/assets/dynamic/p117598535.png']);
    fireEvent.error(container.querySelectorAll('img')[1]!);
    expect(container.querySelectorAll('img')[1]!.getAttribute('src')).toBe('/panel/assets/players/158023.png');
  });
  it('shows the six face stats on catalogue cards', () => {
    const page = render(<FutCard size="catalogue" art={{ rating: 93, rareflag: 1, name: 'Messi' }} assets={{ frames: [] }}
      stats={['PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY'].map((label, index) => ({ label, value: 80 + index }))} />);
    for (const label of ['PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY']) expect(page.getByText(label)).toBeTruthy();
    expect(page.getByText('85')).toBeTruthy();
  });
  it('uses short card names while retaining surname prefixes supplied by the API', () => {
    for (const [name, expected] of [['Cristiano Ronaldo', 'Ronaldo'], ['Roberto Firmino', 'Firmino'], ['Neymar Jr', 'Neymar']] as const) {
      const page = render(<FutCard art={{ name, rating: 90, rareflag: 1 }} assets={{ frames: [] }} />);
      expect(page.container.textContent).toContain(expected);
      expect(page.container.textContent).not.toContain(name);
      page.unmount();
    }
    const page = render(<FutCard art={{ name: 'Kevin De Bruyne', cardName: 'De Bruyne', rating: 90, rareflag: 1 }} assets={{ frames: [] }} />);
    expect(page.container.textContent).toContain('De Bruyne');
  });
  it('picks the colour band FIFA uses (65 and 75 cuts)', () => {
    expect([band(64), band(65), band(74), band(75)]).toEqual(['bronze', 'silver', 'silver', 'gold']);
  });

  it('picks the frame of the rarity and band, or the rarity\'s only frame', () => {
    expect(frameFor(0, 70, FRAMES)).toBe('0_silver.png');
    expect(frameFor(3, 60, FRAMES)).toBe('3_bronze.png');
    expect(frameFor(12, 98, FRAMES)).toBe('12_icon.png');
    expect(frameFor(1, 60, FRAMES)).toBe('1_gold.png');
    expect(frameFor(11, 98, FRAMES)).toBeNull();
  });

  it('uses dark text on light frames and a glow under light text', () => {
    expect(cardLook(12, 98)).toMatchObject({ ink: '#3a2e10', glow: 'none' });
    expect(cardLook(3, 70).ink).toBe('#dde2e7');
    expect(cardLook(55, 90).glow).not.toBe('none');
    expect(cardLook(0, 80)).toMatchObject({ ink: '#2a2006', glow: 'none' });
  });

  it('draws nothing without images, and the special image before the base one', () => {
    const art = { resourceId: 117598535, assetId: 158023, rareflag: 5, rating: 98, name: 'Messi', position: 'RW' };
    expect(render(<FutCard art={art} assets={null} />).container.innerHTML).toBe('');
    const { container, getByRole } = render(<FutCard art={art} assets={{ baseUrl: '/panel/assets/', frames: FRAMES }} />);
    expect(getByRole('img').getAttribute('aria-label')).toBe('Messi, 98 RW');
    const face = [...container.querySelectorAll('img')].map((image) => image.getAttribute('src'));
    expect(face).toContain('/panel/assets/dynamic/p117598535.png');
  });

  it('renders published uploads first and resets the fallback when the selected head changes', () => {
    const art = { resourceId: 117598535, assetId: 158023, rareflag: 5, rating: 98, name: 'Messi', customHeadId: 42 };
    const assets = { baseUrl: '/panel/assets/', frames: FRAMES };
    const { container, rerender } = render(<FutCard art={art} assets={assets} />);
    const face = () => [...container.querySelectorAll('img')].find((img) => img.src.includes('/heads/') || img.src.includes('/dynamic/') || img.src.includes('/players/'))!;
    expect(face().getAttribute('src')).toBe('/panel/heads/42.png?texture=2');
    fireEvent.error(face());
    expect(face().getAttribute('src')).toBe('/panel/assets/players/158023.png');
    rerender(<FutCard art={{ ...art, customHeadId: 43 }} assets={assets} />);
    expect(face().getAttribute('src')).toBe('/panel/heads/43.png?texture=2');
  });
});
