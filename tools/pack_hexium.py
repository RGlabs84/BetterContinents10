"""Build the Hexium upload zip for a tagged release.

The zip holds HexiumDist's package files, the built DLL, the guide PDF with its GPL-3.0 text, the
alt-biome colour scheme (the Krita/GIMP palette and the printable chart from palettes/), and the
exact source of the release: LGPL-2.1 section 4 wants the source to travel with the DLL.

Before running: build the mod, copy the DLL to HexiumDist/plugins/, put the guide at
HexiumDist/BetterContinents-Guide.pdf (or pass its path), and tag the release commit.

Usage: python3 tools/pack_hexium.py v0.8.0 [path/to/BetterContinents-Guide.pdf]

A branch or commit works in place of the tag, for a pre-release package to test before tagging.
"""
import json
import pathlib
import subprocess
import sys
import zipfile

REPO = pathlib.Path(__file__).resolve().parent.parent
DIST = REPO / 'HexiumDist'
EPOCH = (1980, 1, 1, 0, 0, 0)

tag = sys.argv[1]
pdf = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else DIST / 'BetterContinents-Guide.pdf'
version = json.loads((DIST / 'manifest.json').read_text(encoding='utf-8'))['version_number']
is_tag = subprocess.run(['git', '-C', str(REPO), 'rev-parse', '-q', '--verify', f'refs/tags/{tag}'],
                        capture_output=True).returncode == 0
if not is_tag:
    print(f'{tag} is not a tag: building a pre-release package from it')
elif tag.lstrip('v') != version:
    sys.exit(f'tag {tag} does not match manifest version {version}')

# Art sources in misc/ are not part of the build, and HexiumDist is the package itself.
source = subprocess.run(['git', '-C', str(REPO), 'archive', '--format=zip',
                         f'--prefix=BetterContinents-{version}/', tag, '.',
                         ':(exclude)misc', ':(exclude)HexiumDist'],
                        capture_output=True, check=True).stdout

members = {name: (DIST / name).read_bytes() for name in (
    'manifest.json', 'README.md', 'CHANGELOG.md', 'LICENSE.md', 'THIRD-PARTY-NOTICES.txt',
    'GUIDE-LICENSE.txt', 'icon.png')}
members['plugins/BetterContinents.dll'] = (DIST / 'plugins/BetterContinents.dll').read_bytes()
members['BetterContinents-Guide.pdf'] = pdf.read_bytes()
members['BetterContinents.gpl'] = (REPO / 'palettes/BetterContinents.gpl').read_bytes()
members['altbiome-palette.png'] = (REPO / 'palettes/altbiome-palette.png').read_bytes()
members['BetterContinents-source.zip'] = source

out = DIST / f'BetterContinents-v{version}-hexium.zip'
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name, data in members.items():
        info = zipfile.ZipInfo(name, EPOCH)
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, data)
print(f'{out}: {out.stat().st_size / 1e6:.1f} MB')
