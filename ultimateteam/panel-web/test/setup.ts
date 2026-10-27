// Unmounts what each test rendered (Testing Library only does this by itself with Vitest globals on).
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

// jsdom has no scrolling layout; panel overlays call this browser API when opened.
window.scrollTo = () => {};

afterEach(() => cleanup());
