/** Standalone rotations stay outside promotional campaigns. Native TOTW codes also cover old category metadata. */
export const standardPromoCodes = ['bronze_common', 'bronze_rare', 'silver_common', 'silver_rare', 'gold_common', 'gold_rare'];
export const iconPromoCodes = ['base_icons', 'mid_icons', 'prime_icons', 'prime_icon_moments'];
export const isStandardPromo = (code: string) => standardPromoCodes.includes(code);
export const isIconPromo = (code: string) => iconPromoCodes.includes(code);
export const isTotwPromo = (promo: { code: string; category?: string }) => promo.category === 'totw' || /^totw_\d+$/.test(promo.code) || promo.code === 'silver_totw';
export const isCampaignPromo = (promo: { code: string; category?: string }) => !isStandardPromo(promo.code) && !isIconPromo(promo.code) && !isTotwPromo(promo) && !['fut_champions', 'developer_cards'].includes(promo.code);
