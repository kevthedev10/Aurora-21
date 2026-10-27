"""Offline BC2/BC3 encoding with alpha-weighted RGB palette fitting.

Each colour block has two RGB565 endpoints and four interpolated colours.
Choose nearest palette entries, then refine endpoints by weighted least squares.
Transparent RGB must not influence the fit. This is a development utility only.
"""
import numpy as np


def quantize(rgb):
    values = np.floor(np.clip(rgb, 0, 255) * np.array([31, 63, 31]) / 255 + .5).astype(np.uint16)
    packed = (values[..., 0] << 11) | (values[..., 1] << 5) | values[..., 2]
    decoded = np.stack(((values[..., 0] << 3) | (values[..., 0] >> 2),
                        (values[..., 1] << 2) | (values[..., 1] >> 4),
                        (values[..., 2] << 3) | (values[..., 2] >> 2)), axis=-1)
    return packed, decoded.astype(float)


def colors(rgb, weights):
    visible = weights > 0
    lo = np.min(np.where(visible[..., None], rgb, 255), axis=1)
    hi = np.max(np.where(visible[..., None], rgb, 0), axis=1)
    total = np.maximum(weights.sum(axis=1), 1e-12)
    mean = (rgb * weights[..., None]).sum(axis=1) / total[:, None]
    centred = rgb - mean[:, None]
    covariance = np.einsum('npi,npj,np->nij', centred, centred, weights)
    axis = np.linalg.eigh(covariance)[1][:, :, -1]
    projection = np.einsum('npi,ni->np', centred, axis)
    rows = np.arange(len(rgb))
    p0 = rgb[rows, np.argmax(np.where(visible, projection, -np.inf), axis=1)]
    p1 = rgb[rows, np.argmin(np.where(visible, projection, np.inf), axis=1)]
    best_error = np.full(len(rgb), np.inf)
    best = np.zeros((len(rgb), 8), dtype=np.uint8)
    blend = np.array([1, 0, 2/3, 1/3])
    for seed0, seed1 in ((p0, p1), (hi, lo)):
        end0, end1 = seed0, seed1
        for _ in range(8):
            code0, end0 = quantize(end0); code1, end1 = quantize(end1)
            swap = code0 < code1
            code0, code1 = np.maximum(code0, code1), np.minimum(code0, code1)
            end0, end1 = np.where(swap[:, None], end1, end0), np.where(swap[:, None], end0, end1)
            palette = np.stack((end0, end1, np.floor((2*end0 + end1)/3), np.floor((end0 + 2*end1)/3)), axis=1)
            distances = ((rgb[:, :, None] - palette[:, None])**2).sum(axis=-1)
            indices = distances.argmin(axis=-1)
            error = (np.take_along_axis(distances, indices[..., None], axis=-1)[..., 0] * weights).sum(axis=1)
            packed_indices = (indices.astype(np.uint32) << (2*np.arange(16, dtype=np.uint32))).sum(axis=1).astype('<u4')
            blocks = np.concatenate((np.stack((code0, code1), axis=1).astype('<u2').view(np.uint8).reshape(-1, 4),
                                     packed_indices.view(np.uint8).reshape(-1, 4)), axis=1)
            better = error < best_error
            best[better] = blocks[better]; best_error[better] = error[better]
            t = blend[indices]; u = 1-t
            a = (weights*t*t).sum(axis=1); b = (weights*t*u).sum(axis=1); c = (weights*u*u).sum(axis=1)
            rhs0 = (rgb*(weights*t)[..., None]).sum(axis=1)
            rhs1 = (rgb*(weights*u)[..., None]).sum(axis=1)
            determinant = a*c-b*b
            valid = determinant > 1e-10
            divisor = np.where(valid, determinant, 1)[:, None]
            end0 = np.where(valid[:, None], (rhs0*c[:, None]-rhs1*b[:, None])/divisor, end0)
            end1 = np.where(valid[:, None], (rhs1*a[:, None]-rhs0*b[:, None])/divisor, end1)
    return best


def alpha_bc3(alpha):
    lo, hi = alpha.min(axis=1), alpha.max(axis=1)
    best_error = np.full(len(alpha), np.inf)
    best = np.zeros((len(alpha), 8), dtype=np.uint8)
    for first, second, seven in ((hi, lo, True), (lo, hi, False)):
        palette = [first, second]
        denominator = 7 if seven else 5
        for i in range(1, denominator):
            palette.append(np.floor(((denominator-i)*first + i*second)/denominator))
        if not seven:
            palette.extend((np.zeros(len(alpha)), np.full(len(alpha), 255)))
        palette = np.stack(palette, axis=1)
        distances = (alpha[:, :, None]-palette[:, None])**2
        indices = distances.argmin(axis=-1)
        error = np.take_along_axis(distances, indices[..., None], axis=-1)[..., 0].sum(axis=1)
        code = first.astype(np.uint64) | (second.astype(np.uint64) << np.uint64(8))
        code |= (indices.astype(np.uint64) << (16+3*np.arange(16, dtype=np.uint64))).sum(axis=1)
        better = error < best_error
        best[better] = code.astype('<u8').view(np.uint8).reshape(-1, 8)[better]
        best_error[better] = error[better]
    return best


def encode(image, fourcc):
    pixels = np.asarray(image.convert('RGBA'))
    h, w = pixels.shape[:2]
    pixels = np.pad(pixels, ((0, (-h) % 4), (0, (-w) % 4), (0, 0)))
    blocks = pixels.reshape((h+3)//4, 4, (w+3)//4, 4, 4).transpose(0, 2, 1, 3, 4).reshape(-1, 16, 4).astype(float)
    alpha = blocks[:, :, 3]
    colour = colors(blocks[:, :, :3], (alpha/255)**2)
    if fourcc == b'DXT5':
        transparency = alpha_bc3(alpha)
    elif fourcc == b'DXT3':
        levels = np.floor(alpha*15/255+.5).astype(np.uint64)
        code = (levels << (4*np.arange(16, dtype=np.uint64))).sum(axis=1)
        transparency = code.astype('<u8').view(np.uint8).reshape(-1, 8)
    else:
        raise ValueError('Only reviewed BC2/BC3 textures are supported')
    return np.concatenate((transparency, colour), axis=1).tobytes()
