"""Rasterize the currency SVGs using Python/Pillow, without generated artwork.

The deliberately small SVG vocabulary is M/L/C/Z filled paths and one gradient.
Unsupported markup fails rather than silently rendering something different.
"""
import argparse
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw


def render(source, size):
    root = ET.parse(source).getroot()
    box = [float(n) for n in root.attrib['viewBox'].split()]
    if len(box) != 4 or box[:2] != [0, 0] or min(box[2:]) <= 0:
        raise ValueError('Unsupported SVG viewBox')
    width, height = box[2:]
    scale = size / max(width, height)
    dimensions = (round(width * scale), round(height * scale))
    canvas = Image.new('RGBA', dimensions)
    gradients = {}
    for element in root:
        tag = element.tag.rsplit('}', 1)[-1]
        if tag == 'defs':
            continue
        if tag != 'path':
            raise ValueError('Unsupported SVG element: ' + tag)
        tokens = re.findall(r'[A-Za-z]|-?\d+(?:\.\d+)?', element.attrib['d'])
        points, pos = [], 0
        while pos < len(tokens):
            command = tokens[pos]; pos += 1
            if command in ('M', 'L'):
                points.append((float(tokens[pos]), float(tokens[pos + 1]))); pos += 2
            elif command == 'C':
                p0 = points[-1]
                p1, p2, p3 = [(float(tokens[pos + n]), float(tokens[pos + n + 1])) for n in (0, 2, 4)]
                pos += 6
                for n in range(1, 65):
                    t = n / 64; u = 1 - t
                    points.append(tuple(u**3*p0[c] + 3*u*u*t*p1[c] + 3*u*t*t*p2[c] + t**3*p3[c] for c in (0, 1)))
            elif command != 'Z':
                raise ValueError('Unsupported SVG path command: ' + command)
        mask = Image.new('L', dimensions)
        ImageDraw.Draw(mask).polygon([(x * scale, y * scale) for x, y in points], fill=255)
        fill = element.attrib['fill']
        if fill == 'url(#rim)':
            gradient = root.find('.//{*}linearGradient')
            if float(gradient.attrib['x1']) != 0 or float(gradient.attrib['x2']) != width or gradient.attrib['y1'] != gradient.attrib['y2']:
                raise ValueError('Unsupported SVG gradient direction')
            stops = list(gradient)
            colors = [tuple(bytes.fromhex(stop.attrib['stop-color'][1:])) for stop in stops]
            if fill not in gradients:
                layer = Image.new('RGBA', dimensions)
                draw = ImageDraw.Draw(layer)
                for x in range(dimensions[0]):
                    color = tuple(round(a + (b-a)*x/(dimensions[0]-1)) for a,b in zip(*colors)) + (255,)
                    draw.line((x, 0, x, dimensions[1]-1), fill=color)
                gradients[fill] = layer
            layer = gradients[fill]
        else:
            layer = Image.new('RGBA', dimensions, fill)
        canvas.paste(layer, (0, 0), mask)
    return canvas


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    render(args.source, 1024).save(args.output)
