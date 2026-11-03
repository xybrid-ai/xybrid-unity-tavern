#!/usr/bin/env python3
"""Restore local dependencies from a licensed installation; never download paid art."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def safe_path(root, relative):
    path = root / relative
    if Path(relative).is_absolute() or '..' in Path(relative).parts:
        raise ValueError('Invalid manifest path: ' + relative)
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--from-project', type=Path, help='Local licensed asset installation with matching GUIDs')
    parser.add_argument('--verify-hashes', action='store_true', help='Read model/plugin files to verify recorded hashes')
    args = parser.parse_args()
    manifest = json.loads((ROOT / 'local-assets.json').read_text())
    failures = []
    for entry in manifest['files']:
        relative = entry['path']
        destination = safe_path(ROOT, relative)
        if args.from_project and not destination.exists():
            source = safe_path(args.from_project.resolve(), relative)
            if source.is_file():
                destination.parent.mkdir(parents=True, exist_ok=True)
                # APFS cloning saves disk; each project still owns an independent file.
                cloned = sys.platform == 'darwin' and subprocess.run(
                    ['cp', '-c', '-p', str(source), str(destination)],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
                if not cloned:
                    shutil.copy2(source, destination)
        if not destination.is_file():
            failures.append('missing: ' + relative)
        elif destination.stat().st_size != entry['bytes']:
            failures.append('size differs: ' + relative)
        elif args.verify_hashes and entry.get('sha256') and digest(destination) != entry['sha256']:
            failures.append('hash differs: ' + relative)
        elif relative.endswith('.meta') and entry.get('sha256') and digest(destination) != entry['sha256']:
            failures.append('GUID/import settings differ: ' + relative)
    for failure in failures:
        print(failure)
    print(f"Checked {len(manifest['files'])} local files; {len(failures)} unresolved.")
    return bool(failures)


if __name__ == '__main__':
    sys.exit(main())
