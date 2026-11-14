#!/usr/bin/env python3
"""Generate public reference vectors INDEPENDENTLY of the library under test.

Nothing in this file imports, reads or executes SimdImaging. Expected bytes are derived from
first principles (an identity warp is a byte-for-byte copy in every working format), so the
vectors can serve as an oracle rather than as a snapshot of current behaviour.

    python tests/Fixtures/vectors/generate-vectors.py

Writes <case>.json and <case>.sha256. Re-running is deterministic: same bytes, same hashes.
Adding a NEW vector is allowed; editing an existing expected value to make a test pass is not
(AGENTS.md "Never regenerate a golden to make a test pass").
"""
from __future__ import annotations

import hashlib
import json
import struct
from pathlib import Path

OUT = Path(__file__).resolve().parent
WIDTH, HEIGHT = 5, 4

# A deterministic pattern whose 16-bit samples deliberately cross 8-bit boundaries, and whose
# float samples include HDR (>1), negative and out-of-range values. Those are exactly the cases
# that a byte-only sampling intermediate destroys.
FLOAT_VALUES = (0.0, 0.25, 1.0, 4.0, -0.25, 12.5, 0.8038, 2.0)


def rgba8() -> bytes:
    return bytes((i * 37 + c * 61 + 11) & 0xFF
                 for i in range(WIDTH * HEIGHT) for c in range(4))


def rgba16() -> bytes:
    return b"".join(struct.pack("<4H", *[((i * 1031 + c * 7919 + 13) & 0xFFFF)
                                         for c in range(4)])
                    for i in range(WIDTH * HEIGHT))


def rgba32f() -> bytes:
    return b"".join(struct.pack("<4f", *[FLOAT_VALUES[(i * 3 + c) % len(FLOAT_VALUES)]
                                         for c in range(4)])
                    for i in range(WIDTH * HEIGHT))


CASES = (
    ("warp-identity-rgba8", "Rgba8", "D0", rgba8),
    ("warp-identity-rgba16", "Rgba16", "D0", rgba16),
    ("warp-identity-rgba32f", "Rgba32F", "D1", rgba32f),
)


def main() -> None:
    for case_id, fmt, determinism, make in CASES:
        data = make()
        # Identity warp: the destination pixel at (x, y) reads the source at (x + 0.5, y + 0.5).
        # With a pixel-centre phase and bilinear weights, the four taps collapse to one weight of
        # 1.0 and three of 0.0, so the output equals the input byte for byte in every format.
        expected = data
        doc = {
            "id": case_id,
            "generator": "generate-vectors.py",
            "determinism": determinism,
            "tolerance": None,
            "input": {
                "width": WIDTH,
                "height": HEIGHT,
                "format": fmt,
                "bytes": data.hex().upper(),
                "sha256": hashlib.sha256(data).hexdigest().upper(),
            },
            "params": {
                "op": "Warp",
                "map": "identity",
                "resampler": "Bilinear",
                "phase": "pixel-centre",
                "reference": "independent: identity map selects exactly one tap at weight 1.0",
            },
            "expected": {
                "bytes": expected.hex().upper(),
                "sha256": hashlib.sha256(expected).hexdigest().upper(),
            },
        }
        (OUT / f"{case_id}.json").write_text(
            json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        (OUT / f"{case_id}.sha256").write_text(
            hashlib.sha256(expected).hexdigest().upper() + "\n", encoding="utf-8")
        print(f"{case_id}: {len(expected)} bytes sha256={doc['expected']['sha256'][:16]}...")


def midpoint_vector() -> None:
    """Independent encoded-domain bilinear midpoint for the M8 WithOptions contract.

    Two Rgba8 pixels (black and white, alpha 255) sampled at the exact midpoint give 0.5 in the
    encoded domain; round-half-up at 8 bits is 128 on every channel. No floating-point ambiguity.
    """
    case_id = "sample-options-encoded-midpoint"
    data = bytes([0, 0, 0, 255, 255, 255, 255, 255])
    expected = bytes([128, 128, 128, 255])
    doc = {
        "id": case_id,
        "generator": "generate-vectors.py",
        "determinism": "D1",
        "tolerance": None,
        "input": {
            "width": 2,
            "height": 1,
            "format": "Rgba8",
            "bytes": data.hex().upper(),
            "sha256": hashlib.sha256(data).hexdigest().upper(),
        },
        "params": {
            "op": "WarpWithOptions",
            "map": "midpoint (source x = 1.0)",
            "resampler": "Bilinear",
            "domain": "Encoded",
            "alpha": "Straight",
            "phase": "pixel-centre",
            "expectedWidth": 1,
            "expectedHeight": 1,
            "reference": "independent: (0 + 255) / 2 in the encoded domain, round-half-up = 128",
        },
        "expected": {
            "bytes": expected.hex().upper(),
            "sha256": hashlib.sha256(expected).hexdigest().upper(),
        },
    }
    (OUT / f"{case_id}.json").write_text(
        json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (OUT / f"{case_id}.sha256").write_text(
        hashlib.sha256(expected).hexdigest().upper() + "\n", encoding="utf-8")
    print(f"{case_id}: {len(expected)} bytes sha256={doc['expected']['sha256'][:16]}...")


def f32(x: float) -> float:
    """Round a Python float (double) to IEEE-754 binary32 — the precision the library works in."""
    return struct.unpack("<f", struct.pack("<f", x))[0]


def write_vector(case_id: str, determinism: str, data: bytes, expected: bytes, params: dict,
                 width: int | None = None, height: int | None = None) -> None:
    doc = {
        "id": case_id,
        "generator": "generate-vectors.py",
        "determinism": determinism,
        "tolerance": None,
        "input": {
            "width": width if width is not None else (2 if len(data) == 8 else 1),
            "height": height if height is not None else 1,
            "format": "Rgba8",
            "bytes": data.hex().upper(),
            "sha256": hashlib.sha256(data).hexdigest().upper(),
        },
        "params": params,
        "expected": {
            "bytes": expected.hex().upper(),
            "sha256": hashlib.sha256(expected).hexdigest().upper(),
        },
    }
    (OUT / f"{case_id}.json").write_text(
        json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (OUT / f"{case_id}.sha256").write_text(
        hashlib.sha256(expected).hexdigest().upper() + "\n", encoding="utf-8")
    print(f"{case_id}: {len(expected)} bytes sha256={doc['expected']['sha256'][:16]}...")


def tap_bilinear_phase_vector() -> None:
    """Independent 0.37-phase bilinear tap through the shared TapPlanBuilder weights.

    Source 2×1 Rgba8 (black | white). WarpWithOptions maps the destination centre (areal 0.5) to
    source areal 0.5 + f32(0.37). FillAt yields taps (0, 1) with weights 1-|0.5-c| and 1-|1.5-c|,
    whose double sum is exactly 1.0, so normalisation is a no-op and the float weights are
    f32(0.6300000053...) / f32(0.3699999946...). Straight accumulation of [0,1] samples then gives
    r = f32(0.37), and ToByte truncates f32(f32(r·255) + 0.5) = 94.8499… → 94 on every colour
    channel; alpha sums to 1.0 → 255.
    """
    import math
    case_id = "warp.tap.bilinear-phase"
    data = bytes([0, 0, 0, 255, 255, 255, 255, 255])

    centre = 0.5 + f32(0.37)                    # areal source centre for the destination centre
    start = math.ceil(centre - 1.5)             # radius 1, pixel-centre phase
    assert start == 0, start
    w0 = 1.0 - abs(0.5 - centre)                # tap x=0
    w1 = 1.0 - abs(1.5 - centre)                # tap x=1
    total = w0 + w1                             # exactly 1.0 in double arithmetic
    w0f = f32(w0 / total)
    w1f = f32(w1 / total)
    # Straight accumulation on [0,1]-normalised samples (y tap at weight 1, x taps 0 and 1).
    r = f32(f32(0.0 * w0f) + f32(1.0 * w1f))
    scaled = f32(f32(r * 255.0) + 0.5)          # ToByte: round-half-up, truncated
    expected_byte = int(scaled)
    assert expected_byte == 94, expected_byte
    expected = bytes([expected_byte, expected_byte, expected_byte, 255])
    write_vector(case_id, "D1", data, expected, {
        "op": "WarpWithOptions",
        "map": "sx = x + 0.37, sy = y (destination 1×1)",
        "resampler": "Bilinear",
        "domain": "Encoded",
        "alpha": "Straight",
        "phase": "pixel-centre, areal centre 0.5 + f32(0.37)",
        "expectedWidth": 1,
        "expectedHeight": 1,
        "reference": "independent: FillAt weights with double sum exactly 1.0, straight Σw·c, ToByte round-half-up",
    })


def resize_background_edge_vector() -> None:
    """Independent Edge=Background resize: a constant field replaces every out-of-bounds tap.

    Source 1×1 Rgba8 white, magnified to 2×2 with Bilinear and a straight-black field. The resize
    phase puts axis centres at 0.25 / 0.75; every tap except (x=0, y=0) — weight 0.75·0.75 = 0.5625
    exactly — falls outside the source and becomes the field, and weights are NOT renormalised.
    White contributes 1.0·0.5625 → ToByte = 143 on every colour channel; the field's opaque alpha
    sums the total to 1.0 → 255. All four destination pixels share the same tap geometry by symmetry.
    """
    case_id = "resize.background-edge"
    data = bytes([255, 255, 255, 255])
    white_weight = f32(f32(0.75) * f32(0.75))   # 0.5625, exact in binary32
    scaled = f32(f32(white_weight * 255.0) + 0.5)
    byte = int(scaled)
    assert byte == 143, byte
    expected = bytes([byte, byte, byte, 255]) * 4
    write_vector(case_id, "D1", data, expected, {
        "op": "ResizeWithOptions",
        "map": "1×1 → 2×2, resize phase centre (i + 0.5) / scale",
        "resampler": "Bilinear",
        "domain": "Encoded",
        "alpha": "Straight",
        "edge": "Background (field = opaque black)",
        "expectedWidth": 2,
        "expectedHeight": 2,
        "reference": "independent: only the (0,0) tap at exact weight 0.5625 is in bounds; no renormalisation",
    })


def ewa_identity_vector() -> None:
    """Independent elliptical-Gaussian vector: identity Jacobian, encoded domain, 0.37-free phase.

    Source 2×1 Rgba8 (black | white) sampled by AffineWithOptions(Identity, Elliptical) at the
    destination centre (areal 0.5 → source areal 0.5). The footprint window clips to taps x=0,1
    with weights exp(0)=1 and exp(-0.5·1²); their double sum normalises to
    f32(1/1.60653…)=0.62246 and f32(0.60653/1.60653)=0.37754. Straight Σw·c on [0,1] samples gives
    r = 0.37754 → ToByte truncates f32(f32(r·255)+0.5) = 96.77… → 96 on every colour channel.
    """
    import math
    case_id = "ewa.identity"
    data = bytes([0, 0, 0, 255, 255, 255, 255, 255])
    total = 1.0 + math.exp(-0.5)
    w1 = f32(math.exp(-0.5) / total)
    scaled = f32(f32(w1 * 255.0) + 0.5)
    byte = int(scaled)
    assert byte == 96, byte
    expected = bytes([byte, byte, byte, 255])
    write_vector(case_id, "D1", data, expected, {
        "op": "AffineWithOptions",
        "map": "identity (destination centre areal 0.5 → source areal 0.5)",
        "resampler": "Bilinear",
        "aa": "Elliptical (J = I, kernelScale 1, tier budget 8)",
        "domain": "Encoded",
        "alpha": "Straight",
        "expectedWidth": 1,
        "expectedHeight": 1,
        "reference": "independent: exp(-0.5·q) weights, double-normalised then f32, straight Σw·c",
    })


def ewa_minify_vector() -> None:
    """Independent symmetric minification: J = diag(2,2) on a 2×2 quad-centre sample.

    Source 2×2 Rgba8 (black, white / white, black) via AffineWithOptions(Scale(0.5), Elliptical):
    the destination centre (areal 0.5) maps to source areal (1, 1), the exact junction of the four
    pixels. Q = J·Jᵀ = diag(4,4) so R = Q⁻¹ = diag(1/4,1/4); every clipped tap (x,y ∈ {0,1}) has
    d = ±0.5 and q = dᵀRd = 0.125, so all four weights are exp(-0.0625) and normalise to exactly
    0.25 in binary32. Straight Σw·c averages the four samples to 0.5 → ToByte = 128 — a
    zero-ambiguity symmetric oracle that is invariant to the footprint formula (equal weights).
    """
    import math
    case_id = "ewa.minify"
    data = bytes([0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255])
    assert f32(math.exp(-0.0625) / (4.0 * math.exp(-0.0625))) == 0.25
    expected = bytes([128, 128, 128, 255])
    write_vector(case_id, "D1", data, expected, {
        "op": "AffineWithOptions",
        "map": "Scale(0.5) forward (destination centre areal 0.5 → source areal (1,1))",
        "resampler": "Bilinear",
        "aa": "Elliptical (J = diag(2,2), R = diag(1/4,1/4), tier budget 16)",
        "domain": "Encoded",
        "alpha": "Straight",
        "expectedWidth": 1,
        "expectedHeight": 1,
        "reference": "independent: four equal exp(-0.0625) taps normalise to exact 0.25 each; mean = 0.5",
    }, width=2, height=2)


if __name__ == "__main__":
    main()
    midpoint_vector()
    tap_bilinear_phase_vector()
    resize_background_edge_vector()
    ewa_identity_vector()
    ewa_minify_vector()
