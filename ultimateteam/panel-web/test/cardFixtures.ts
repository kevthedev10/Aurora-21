// Synthetic browser replies with the complete editable value returned by OperatorPanel.CardEditor.cs.
const detailNumbers = [
  'acceleration', 'sprint_speed', 'positioning', 'finishing', 'shot_power', 'long_shots', 'volleys', 'penalties',
  'vision', 'crossing', 'free_kick_accuracy', 'short_passing', 'long_passing', 'curve', 'agility', 'balance',
  'reactions', 'ball_control', 'dribbling', 'composure', 'interceptions', 'heading_accuracy', 'marking',
  'standing_tackle', 'sliding_tackle', 'jumping', 'stamina', 'strength', 'aggression',
  'gk_diving', 'gk_handling', 'gk_kicking', 'gk_reflexes', 'gk_speed', 'gk_positioning',
];
export const editableCard = {
  position: 'RW', preferred_foot: 'Left', work_rate_attack: 'High', work_rate_defense: 'Low',
  rating: 93, weak_foot: 4, skill_moves: 4,
  face_slot_0: 85, face_slot_1: 92, face_slot_2: 91, face_slot_3: 95, face_slot_4: 38, face_slot_5: 65,
  ...Object.fromEntries(detailNumbers.map((key) => [key, key.startsWith('gk_') ? null : 70])),
  head_resource_id: null, custom_head_id: null, traits: [1],
};
export const cardEditorReply = {
  resourceId: 158023, assetId: 158023, name: 'Messi', cardTypeId: 1, card: editableCard,
  art: { resourceId: 158023, assetId: 158023, name: 'Messi', rating: 93, position: 'RW', rareflag: 1, teamId: 241, nationId: 52, leagueId: 53 },
  options: { positions: ['RW', 'ST', 'GK'], feet: ['Left', 'Right'], workRates: ['High', 'Medium', 'Low'],
    cardTypes: [{ id: 1, name: 'Gold rare', level: 'gold', rareflag: 1 }, { id: 2, name: 'Silver rare', level: 'silver', rareflag: 1 },
      { id: 3, name: 'Bronze rare', level: 'bronze', rareflag: 1 }, { id: 4, name: 'Gold common', level: 'gold', rareflag: 0 },
      { id: 6, name: 'Silver common', level: 'silver', rareflag: 0 }, { id: 7, name: 'Bronze common', level: 'bronze', rareflag: 0 },
      { id: 5, name: 'Team of the Year', rareflag: 5 }, { id: 8, name: 'Team of the Week Gold', rareflag: 3, level: 'gold' },
      { id: 9, name: 'Team of the Week Silver', rareflag: 3, level: 'silver' }, { id: 10, name: 'Team of the Week Bronze', rareflag: 3, level: 'bronze' },
      { id: 12, name: 'Icon', rareflag: 12 }, { id: 84, name: 'Icon Moments', rareflag: 84 }, { id: 89, name: 'Icon Swaps 1', rareflag: 89 }],
    traits: [{ id: 1, name: 'Finesse shot', appliesTo: 'outfield' as const }, { id: 15, name: 'GK Long Throw', appliesTo: 'goalkeeper' as const },
      { id: 3, name: 'Injury Prone', appliesTo: 'both' as const }] },
};
export const cardImagesReply = {
  assetId: 158023, basePhoto: '/panel/assets/players/158023.png',
  cards: [
    { resourceId: 158023, name: 'Messi', rating: 93, rarity: 'Gold rare', url: '/panel/assets/dynamic/p158023.png' },
    { resourceId: 117598535, name: 'Messi', rating: 98, rarity: 'Team of the Year', url: '/panel/assets/dynamic/p117598535.png' },
    { resourceId: 16935239, name: 'Messi', rating: 94, rarity: 'Team of the Week', url: null },
  ],
  uploads: [{ headId: 42, label: 'Winter portrait', url: '/panel/heads/42.png' }],
};
