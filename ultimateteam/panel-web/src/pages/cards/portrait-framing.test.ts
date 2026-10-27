import { afterEach, expect, it, vi } from 'vitest';
import { fileAsHead } from './PortraitUpload';

afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('fits the visible cutout rather than its transparent padding and applies position and size', async () => {
  class Reader {
    result = 'data:image/png;base64,AA=='; onload: (() => void) | null = null;
    readAsDataURL() { this.onload?.(); }
  }
  class Picture {
    naturalWidth = 200; naturalHeight = 200; onload: (() => void) | null = null;
    set src(_value: string) { this.onload?.(); }
  }
  vi.stubGlobal('FileReader', Reader); vi.stubGlobal('Image', Picture);
  const pixels = new Uint8ClampedArray(200 * 200 * 4);
  for (let y = 25; y < 175; y++) for (let x = 50; x < 150; x++) pixels[(y * 200 + x) * 4 + 3] = 255;
  const drawImage = vi.fn();
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(function (this: HTMLCanvasElement) {
    return { drawImage, getImageData: () => ({ data: this.width === 200 ? pixels : new Uint8ClampedArray(180 * 180 * 4) }) } as unknown as CanvasRenderingContext2D;
  });
  vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockReturnValue('data:image/png;base64,AAAA');
  await fileAsHead(new File(['synthetic'], 'padded.png'));
  const crop = drawImage.mock.calls.find((call) => call.length === 9)!;
  expect(crop.slice(1, 5)).toEqual([50, 25, 100, 150]);
  expect(crop[5]).toBeCloseTo(90 - 100 * 170 / 150 / 2);
  expect(crop[6]).toBe(165 - 170);
  expect(crop[7]).toBeCloseTo(100 * 170 / 150); expect(crop[8]).toBe(170);
  drawImage.mockClear();
  await fileAsHead(new File(['synthetic'], 'padded.png'), { zoom: 150, x: -12, y: 9, fade: false });
  const moved = drawImage.mock.calls.find((call) => call.length === 9)!;
  expect(moved[5]).toBeCloseTo(90 - 170 / 2 - 12);
  expect(moved[6]).toBe(165 - 255 + 9);
  expect(moved[7]).toBeCloseTo(170); expect(moved[8]).toBe(255);
});
