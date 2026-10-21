"""Writes qr-vectors.txt: QR symbols made by segno, which tests/Tests.cs compares with
src/QrCode.cs. Run it with the repository's test venv (segno):

  python windows/tests/make_qr_vectors.py

Each line: version, level (L M Q H), mask (or "auto"), data length, then the SHA-256 of the
symbol's rows written as "0"/"1" lines ending in "\n". The data is byte i = (i * 7 + 3) % 256.
"fixed" lines fill each version and level with as much data as fits, with a set mask; "auto"
lines let segno pick the version, the level boost and the mask, and the test checks the
same version and level, and the same symbol with segno's mask. The masks the two pick
themselves may differ: their penalty scores differ in details, and any mask is valid.

segno 1.6 pads differently from ISO/IEC 18004 (7.4.10) when the data ends on a codeword
boundary: it adds a whole zero codeword before the pad codewords, where the standard adds
none. Both decode the same, but the symbols differ, so this script gives segno the
standard's padding.
"""

import hashlib
import os

import segno
import segno.encoder


def _standard_padding_bits(buff, version, length):
    if length % 8:
        buff.extend([0] * (8 - length % 8))


segno.encoder.write_padding_bits = _standard_padding_bits

LEVELS = "LMQH"
HERE = os.path.dirname(os.path.abspath(__file__))


def data(length):
    return bytes((i * 7 + 3) % 256 for i in range(length))


def rows(qr):
    return "".join("".join("1" if m else "0" for m in row) + "\n" for row in qr.matrix)


def fits(length, version, level):
    try:
        segno.make(data(length), version=version, error=level, mode="byte", mask=0, boost_error=False)
        return True
    except segno.DataOverflowError:
        return False


def capacity(version, level):
    low, high = 0, 3000
    while low < high:
        middle = (low + high + 1) // 2
        if fits(middle, version, level):
            low = middle
        else:
            high = middle - 1
    return low


def main():
    lines = []
    for version in range(1, 41):
        for index, level in enumerate(LEVELS):
            length = capacity(version, level)
            mask = (version + index) % 8
            qr = segno.make(data(length), version=version, error=level, mode="byte", mask=mask, boost_error=False)
            assert qr.version == version and qr.error == level and qr.mask == mask
            lines.append("fixed %d %s %d %d %s" % (version, level, mask, length,
                                                   hashlib.sha256(rows(qr).encode()).hexdigest()))
    for length in (1, 10, 17, 30, 60, 100, 150, 180, 220, 300, 500):
        for level in "LM":
            qr = segno.make_qr(data(length), error=level, mode="byte")  # make() picks Micro QR for short data
            lines.append("auto %d %s %d %d %s %s" % (qr.version, qr.error, qr.mask, length, level,
                                                      hashlib.sha256(rows(qr).encode()).hexdigest()))
    with open(os.path.join(HERE, "qr-vectors.txt"), "w", encoding="ascii", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print("wrote %d vectors with segno %s" % (len(lines), segno.__version__))


if __name__ == "__main__":
    main()
