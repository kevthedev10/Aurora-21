"""Check encoded blocks with Pillow's independent DDS decoder."""
import io
import struct
import unittest
import numpy as np
from PIL import Image
from bcn import encode


def decoded(image, fourcc):
    header = bytearray(128)
    header[:4] = b'DDS '
    struct.pack_into('<7I', header, 4, 124, 0x81007, image.height, image.width, 16, 0, 0)
    struct.pack_into('<2I', header, 76, 32, 4)
    header[84:88] = fourcc
    struct.pack_into('<I', header, 108, 0x1000)
    body = encode(image, fourcc)
    assert len(body) == ((image.width+3)//4)*((image.height+3)//4)*16
    return np.array(Image.open(io.BytesIO(header+body)).convert('RGBA'))


class EncodingTests(unittest.TestCase):
    def test_bc3_constant_alpha_including_equal_endpoints(self):
        for alpha in (0, 1, 127, 255):
            result = decoded(Image.new('RGBA', (7, 5), (120, 160, 210, alpha)), b'DXT5')
            self.assertEqual(result.shape, (5, 7, 4))
            self.assertTrue(np.all(result[:, :, 3] == alpha))
            if alpha:
                self.assertLessEqual(np.abs(result[:, :, :3].astype(int)-[120, 160, 210]).max(), 5)

    def test_bc2_alpha_nibbles_round_trip(self):
        source = np.full((4, 4, 4), [120, 160, 210, 255], dtype=np.uint8)
        source[:, :, 3] = np.arange(16).reshape(4, 4)*17
        result = decoded(Image.fromarray(source), b'DXT3')
        np.testing.assert_array_equal(result[:, :, 3], source[:, :, 3])

    def test_hidden_rgb_cannot_change_visible_palette(self):
        source = np.full((4, 4, 4), [110, 150, 240, 255], dtype=np.uint8)
        source[:2, :, 3] = 0
        other = source.copy(); other[:2, :, :3] = [255, 0, 255]
        for format in (b'DXT3', b'DXT5'):
            self.assertEqual(encode(Image.fromarray(source), format), encode(Image.fromarray(other), format))
            result = decoded(Image.fromarray(other), format)
            self.assertLessEqual(np.abs(result[2:, :, :3].astype(int)-[110, 150, 240]).max(), 5)
            self.assertTrue(np.all(result[:2, :, 3] == 0))


if __name__ == '__main__':
    unittest.main()
