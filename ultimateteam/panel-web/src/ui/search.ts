/** Match names without requiring accents, while keeping the displayed spelling. */
export const searchText = (value: string | number) => String(value).normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
