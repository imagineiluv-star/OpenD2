"""Independent little-endian rule-v5 state vectors for the three fixed contract scenarios."""
import hashlib
from pathlib import Path
import struct

root = Path(__file__).resolve().parents[1]
def i(value): return struct.pack('<i', value)
def u(value): return struct.pack('<I', value)
def q(value): return struct.pack('<q', value)
def entity(identity, region, x, y, mx=0, my=0, kind=0, hp=100, maximum=100, cooldown=0, mode=0, sequence=0, tick=0):
    return u(identity) + u(region) + b''.join(i(v) for v in (x, y, mx, my, kind, hp, maximum, cooldown, 0, mode)) + u(0) + struct.pack('<Qq', sequence, tick)
def grid(region): return u(region) + i(0) + i(0) + i(10) + i(10) + bytes([1]) * 100
def loot(region): return i(1) + u(2) + i(0) + i(0) + u(0) + i(-1) + u(region) + i(640) + i(384)
vectors = {
    'SimulationContracts.cs': q(4) + u(270369) + b'\0' + i(1) + entity(7, 3, 64, -64, my=-1, sequence=3, tick=3) + i(0) + b'\0' + i(0),
    'CombatContracts.cs': q(1) + u(270369) + b'\1' + grid(1) + i(2) + entity(1, 1, 384, 384, cooldown=12, sequence=2, tick=1) + entity(2, 1, 640, 384, kind=1, hp=0, mode=3) + i(0) + b'\0' + loot(1),
    'WorldContracts.cs': q(5) + u(270369) + b'\1' + grid(1) + i(2) + entity(1, 1, 384, 384, cooldown=10, sequence=5, tick=5) + entity(2, 2, 640, 384, kind=1, hp=0, mode=3) + i(0) + b'\1' + b'\x40' + b'85e4863a4455df1930320c2618a7fba6b2271f61dbcd1b8a81d640086a4e0f3b' + u(1) + u(1) + i(3) + loot(2),
}
layout = hashlib.sha256(i(10) + i(4) + i(0) + b"\0" + i(1) + i(3) + i(1) + b"\0" + i(2) + i(3)).hexdigest()
for name, body in vectors.items():
    digest = hashlib.sha256(i(5) + body + b"\x40" + layout.encode("ascii")).hexdigest()
    print(name, digest)
    assert digest in (root / 'tests/OpenD2.Tests' / name).read_text(), 'Contract must pin the independent rule-v5 vector'
