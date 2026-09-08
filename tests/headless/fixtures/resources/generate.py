"""Regenerate small, synthetic engine fixtures with only Python's standard library."""
import json
from pathlib import Path
import struct
import wave
import zlib

ROOT = Path(__file__).resolve().parent


def png(path, width, height):
    path.parent.mkdir(parents=True, exist_ok=True)
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    rows = b''.join(b'\0' + b''.join(bytes((220, 60 + (y % 8) * 20, 90, 128 if x % 8 < 2 else 255)) for x in range(width)) for y in range(height))
    path.write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))


paths = ['Body', 'Face/Outline', 'Face/Cheeks', 'Eyes/Normal', 'Eyes/Surprised', 'Eyes/Fearful', 'Sweat/On', 'Arm/Rest', 'Arm/Raised']
for actor, size in [('LayeredA', 1024), ('LayeredB', 512)]:
    directory = ROOT / 'characters' / actor
    png(directory / 'atlas.png', size, size)
    layers = [dict(path=path, sprite=path.split('/')[-1], atlas='atlas.png',
                   region=dict(x=(index % 3) * 8, y=(index // 3) * 8, width=8, height=8),
                   pivot=dict(x=.5, y=.5), position=dict(x=0, y=index * .02),
                   scale=dict(x=1, y=1), sortingOrder=index,
                   enabled=path in ['Body', 'Face/Outline', 'Face/Cheeks', 'Eyes/Normal', 'Arm/Rest'],
                   material='Multiply' if path == 'Face/Outline' else 'Default')
              for index, path in enumerate(paths)]
    manifest = dict(schemaVersion=1, id=actor, canvas=dict(width=1, height=1, pixelsPerUnit=100),
                    animated=True, defaultAppearance='Default,ArmR1',
                    compositionMap={'Default':'Eyes>Normal,Sweat-', 'Normal1':'Eyes>Normal,Sweat-',
                                    'Surprised1':'Eyes>Surprised,Sweat>On', 'Fearful1':'Eyes>Fearful,Sweat-',
                                    'ArmR1':'Arm>Rest', 'ArmR3':'Arm>Raised'}, layers=layers)
    (directory / 'layered_character.json').write_text(json.dumps(manifest, indent=2) + '\n')
png(ROOT / 'characters/Plain/1.png', 16, 24)
png(ROOT / 'backgrounds/room.png', 32, 18)
voice = ROOT / 'audio/voice/preload-voice.wav'
voice.parent.mkdir(parents=True, exist_ok=True)
with wave.open(str(voice), 'wb') as output:
    output.setparams((1, 2, 8000, 0, 'NONE', 'not compressed'))
    output.writeframes(b'\0\0' * 800)
