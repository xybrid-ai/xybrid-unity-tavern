#!/usr/bin/env python3
"""Check tracked and trackable files for excluded assets and credentials before commit/release.

    python3 tools/check_repository.py                       # repository policy
    python3 tools/check_repository.py --archive build.zip   # release ZIP text entries

Findings name the file (or archive entry), line and rule. Matched values are never printed.
"""
import argparse
import io
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]

CHUNK = 8 * 1024 * 1024
OVERLAP = 4096  # longer than any reported match; a match starting in the tail is rescanned next chunk
SNIFF = 8192
NESTED_ZIP_LIMIT = 512 * 1024 * 1024
MAX_ZIP_DEPTH = 3

# Credential shapes. Each rule must not match placeholders such as sk_live_xxx,
# "xyb_...", xybrid_api_key=<your key> or a bare xybrid_api_key variable name.
CREDENTIAL_RULES = [
    ('secret key prefix', re.compile(rb'\b(?:sk|rk|xy)_(?:live|test)_[A-Za-z0-9]{16,}')),
    ('Xybrid key prefix', re.compile(rb'\bxyb_[A-Za-z0-9]{20,}')),
    ('provider token', re.compile(
        rb'\b(?:hf_[A-Za-z0-9]{30,}|gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,}'
        rb'|xox[abprs]-[A-Za-z0-9-]{10,}|sk-(?:ant-|proj-)?[A-Za-z0-9_-]{32,}'
        rb'|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35})')),
    ('private key block', re.compile(rb'-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----')),
]
# NAME = value / "name": "value" where the name looks like a credential and the
# value looks like a literal secret rather than a lookup or a variable name.
ASSIGNMENT = re.compile(
    rb'(?i)(?:api[_-]?key|secret|token|passw(?:or)?d)[A-Za-z0-9_.-]{0,40}["\']?'
    rb'[ \t]*[:=][ \t]*["\']?([A-Za-z0-9_\-+/=.]{20,})')
PLACEHOLDER = re.compile(rb'(?i)xxx|your|example|placeholder|changeme|redacted|\.\.\.')

# Removed serialized field: its YAML entry must not come back in scenes, prefabs
# or assets, and the C# field must not be reintroduced.
OBSOLETE_FIELD = re.compile(rb'\b_telemetryApiKeyEnvVar\b')
UNITY_YAML_SUFFIXES = {
    '.unity', '.prefab', '.asset', '.mat', '.controller', '.overridecontroller', '.anim',
    '.playable', '.preset', '.lighting', '.mask', '.signal', '.spriteatlas', '.terrainlayer',
}
NESTED_ARCHIVE_SUFFIXES = {'.unitypackage', '.tar', '.tgz', '.gz', '.bz2', '.xz', '.7z', '.rar', '.jar', '.aar'}
ENV_TEMPLATES = {'.env.example', '.env.sample', '.env.template'}


def is_text(head):
    return b'\0' not in head


def checks_obsolete_field(name, head):
    suffix = PurePosixPath(name).suffix.lower()
    return suffix == '.cs' or suffix in UNITY_YAML_SUFFIXES or head.startswith(b'%YAML')


def _assignment_is_secret(value):
    return (re.search(rb'[0-9]', value) and re.search(rb'[A-Za-z]', value)
            and not PLACEHOLDER.search(value))


def scan_stream(read, obsolete_field=False):
    """Return sorted (line, rule) findings for a binary-safe read(n) callable."""
    rules = list(CREDENTIAL_RULES)
    if obsolete_field:
        rules.append(('obsolete serialized telemetry field', OBSOLETE_FIELD))
    findings = set()
    buffer = b''
    lines_before = 0
    while True:
        block = read(CHUNK)
        buffer += block
        final = not block
        limit = len(buffer) if final else max(0, len(buffer) - OVERLAP)

        def record(rule, start):
            if start < limit or final:
                findings.add((lines_before + buffer.count(b'\n', 0, start) + 1, rule))

        for rule, pattern in rules:
            for match in pattern.finditer(buffer):
                record(rule, match.start())
        for match in ASSIGNMENT.finditer(buffer):
            if _assignment_is_secret(match.group(1)):
                record('credential assignment', match.start())
        if final:
            return sorted(findings)
        lines_before += buffer.count(b'\n', 0, limit)
        buffer = buffer[limit:]


def scan_file(path, name):
    with path.open('rb') as stream:
        head = stream.read(SNIFF)
        if not is_text(head):
            return []
        stream.seek(0)
        return scan_stream(stream.read, checks_obsolete_field(name, head))


def format_findings(location, findings):
    return [f'{rule}: {location}:{line}' for line, rule in findings]


def check_repository():
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
        is_binary_payload = Path(asset_path).suffix.lower() in binaries
        if is_binary_payload and not (relative.startswith('docs/screenshots/') and Path(asset_path).suffix.lower() in {'.png', '.jpg'}):
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
        failures.extend(format_findings(relative, scan_file(path, relative)))
    print('\n'.join(failures) if failures else
          'Repository policy passed: source only, no LFS, external payloads or credentials.')
    return bool(failures)


def scan_zip(archive, label, failures, stats, depth=0):
    for info in archive.infolist():
        if info.is_dir():
            continue
        location = f'{label}!{info.filename}'
        name = PurePosixPath(info.filename).name
        suffix = PurePosixPath(info.filename).suffix.lower()
        if name == '.env' or (name.startswith('.env.') and name not in ENV_TEMPLATES):
            failures.append('environment file: ' + location)
        try:
            if suffix == '.zip':
                if depth + 1 > MAX_ZIP_DEPTH or info.file_size > NESTED_ZIP_LIMIT:
                    failures.append('nested archive not scanned (too deep or large): ' + location)
                    continue
                with archive.open(info) as stream, zipfile.ZipFile(io.BytesIO(stream.read())) as nested:
                    scan_zip(nested, location, failures, stats, depth + 1)
                continue
            if suffix in NESTED_ARCHIVE_SUFFIXES:
                failures.append('nested archive not scanned (scan its contents separately): ' + location)
                continue
            with archive.open(info) as stream:
                head = stream.read(SNIFF)
                if not is_text(head):
                    stats['binary'] += 1
                    continue
                pending = [head]

                def read(size):
                    return pending.pop() if pending else stream.read(size)

                stats['text'] += 1
                failures.extend(format_findings(location, scan_stream(read, checks_obsolete_field(info.filename, head))))
        except (zipfile.BadZipFile, RuntimeError, NotImplementedError, OSError, EOFError) as error:
            # Encrypted, corrupt or unsupported entries fail closed; the exception text is not printed.
            failures.append(f'unreadable entry ({type(error).__name__}): ' + location)


def check_archives(paths):
    failures = []
    stats = {'text': 0, 'binary': 0}
    for path in paths:
        try:
            with zipfile.ZipFile(path) as archive:
                scan_zip(archive, str(path), failures, stats)
        except (zipfile.BadZipFile, OSError) as error:
            failures.append(f'unreadable archive ({type(error).__name__}): {path}')
    if failures:
        print('\n'.join(failures))
    else:
        print(f"Archive check passed: {stats['text']} text entries scanned, "
              f"{stats['binary']} binary entries skipped, in {len(paths)} archive(s).")
    return bool(failures)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--archive', action='append', type=Path, metavar='ZIP',
                        help='Scan this release ZIP instead of the repository (repeatable)')
    args = parser.parse_args(argv)
    return check_archives(args.archive) if args.archive else check_repository()


if __name__ == '__main__':
    sys.exit(main())
