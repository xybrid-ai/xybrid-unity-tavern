#!/usr/bin/env python3
"""Check tracked and trackable files for excluded assets before commit/release."""
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    candidates = subprocess.check_output(
        ['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard'], cwd=ROOT
    ).decode().split('\0')
    excluded = {f['path'] for f in json.loads((ROOT / 'local-assets.json').read_text())['files']}
    failures = []
    binaries = {'.gguf', '.xyb', '.onnx', '.dylib', '.so', '.dll', '.unitypackage', '.zip', '.fbx', '.ttf', '.wav', '.png', '.jpg'}
    for relative in sorted(set(candidates) - {''}):
        path = ROOT / relative
        if not path.is_file():
            continue
        asset_path = relative[:-5] if relative.endswith('.meta') else relative
        if relative in excluded or asset_path.startswith(('Assets/Synty/', 'Assets/StreamingAssets/')):
            failures.append('external asset: ' + relative)
        if Path(asset_path).suffix.lower() in binaries and not (relative.startswith('docs/screenshots/') and Path(asset_path).suffix.lower() in {'.png', '.jpg'}):
            failures.append('binary payload: ' + relative)
        if path.stat().st_size > 5 * 1024 * 1024:
            failures.append('file over 5 MiB: ' + relative)
        data = path.read_bytes() if path.stat().st_size <= 25 * 1024 * 1024 else b''
        if data.startswith(b'version https://git-lfs.github.com/spec/v1'):
            failures.append('LFS pointer: ' + relative)
        if path.suffix in {'.unity', '.prefab', '.asset'} and any(
            token in data for token in [b'm_VertexData:', b'm_IndexBuffer:', b'm_ImageData:']
        ):
            failures.append('embedded art data: ' + relative)
        if path.name == '.gitattributes' and b'filter=lfs' in data:
            failures.append('LFS configuration: ' + relative)
    print('\n'.join(failures) if failures else 'Repository policy passed: source only, no LFS or external payloads.')
    return bool(failures)


if __name__ == '__main__':
    sys.exit(main())
