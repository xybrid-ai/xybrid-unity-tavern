"""Synthetic fixtures for tools/check_repository.py.

Credential-shaped values are assembled at runtime from fragments so this file
itself passes the repository check. None of them is a real credential.
"""
import contextlib
import io
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import check_repository as check  # noqa: E402

PREFIXED = 'sk' + '_live_' + 'Ab3dEf6hIj9kLm2nOp5q'
XYBRID_PREFIXED = 'xy' + '_test_' + 'Zz9Yy8Xx7Ww6Vv5Uu4Tt'
ASSIGNED = 'Q7rT2vX9' * 4
OBSOLETE = '_telemetry' + 'ApiKeyEnvVar'
SECRETS = [PREFIXED, XYBRID_PREFIXED, ASSIGNED]


def scan_text(text, name='fixture.txt'):
    data = text.encode()
    return check.scan_stream(io.BytesIO(data).read, check.checks_obsolete_field(name, data[:check.SNIFF]))


class SourceScanTests(unittest.TestCase):
    def test_flags_prefixed_key_without_printing_it(self):
        findings = scan_text('// config\nvar key = "' + PREFIXED + '";\n')
        self.assertEqual(findings, [(2, 'secret key prefix')])
        rendered = '\n'.join(check.format_findings('Assets/Scripts/Foo.cs', findings))
        self.assertIn('Assets/Scripts/Foo.cs:2', rendered)
        self.assertNotIn(PREFIXED, rendered)

    def test_flags_xybrid_test_prefix(self):
        self.assertEqual(scan_text('token ' + XYBRID_PREFIXED), [(1, 'secret key prefix')])

    def test_flags_assigned_api_key_value(self):
        self.assertEqual(scan_text('xybrid_api_key=' + ASSIGNED + '\n'), [(1, 'credential assignment')])
        self.assertEqual(scan_text('{"apiKey": "' + ASSIGNED + '"}'), [(1, 'credential assignment')])

    def test_allows_legitimate_variable_name_and_placeholders(self):
        text = '\n'.join([
            'public const string EnvironmentVariable = "xybrid_api_key";',
            'string apiKey = DotEnv.Get("xybrid_api_key");',
            'export xybrid_api_key="$XYBRID_KEY"',
            'xybrid_api_key=',
            'xybrid_api_key=<your key>',
            'XYBRID_API_KEY=sk_live_xxx',
            'xybrid.init(api_key="xyb_...")',
            'api_key: your_api_key_goes_here_123',
            'm_TokenName: SomeLongIdentifierWithoutDigits',
        ])
        self.assertEqual(scan_text(text, 'notes.md'), [])

    def test_obsolete_field_only_in_serialized_or_csharp_files(self):
        yaml = '%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  ' + OBSOLETE + ': xybrid_api_key\n'
        self.assertEqual(scan_text(yaml, 'Assets/Scenes/A.unity'), [(4, 'obsolete serialized telemetry field')])
        self.assertEqual(scan_text(yaml, 'Assets/Prefabs/A.prefab'), [(4, 'obsolete serialized telemetry field')])
        csharp = '[SerializeField] private string ' + OBSOLETE + ' = "xybrid_api_key";'
        self.assertEqual(scan_text(csharp, 'Assets/Scripts/A.cs'), [(1, 'obsolete serialized telemetry field')])
        self.assertEqual(scan_text('Removed ' + OBSOLETE + ' field.', 'docs/notes.md'), [])

    def test_finds_value_across_chunk_boundaries(self):
        text = ('filler line\n' * 420) + 'key ' + PREFIXED + '\n' + ('x' * 9000) + '\n'
        data = text.encode()
        pieces = [data[i:i + 997] for i in range(0, len(data), 997)]
        findings = check.scan_stream(lambda _size: pieces.pop(0) if pieces else b'')
        self.assertEqual(findings, [(421, 'secret key prefix')])

    def test_repository_scan_skips_binary_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'blob.bin'
            path.write_bytes(b'\0\1\2' + PREFIXED.encode())
            self.assertEqual(check.scan_file(path, 'blob.bin'), [])


class ArchiveScanTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def run_check(self, *paths):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            failed = check.main([arg for path in paths for arg in ('--archive', str(path))])
        return failed, out.getvalue()

    def write_zip(self, name, entries):
        path = self.tmp / name
        with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as archive:
            for entry, data in entries.items():
                archive.writestr(entry, data)
        return path

    def test_reports_entries_without_values(self):
        inner = io.BytesIO()
        with zipfile.ZipFile(inner, 'w') as nested:
            nested.writestr('settings.json', '{"token": "' + XYBRID_PREFIXED + '"}')
        path = self.write_zip('release.zip', {
            'Tavern/.env': 'xybrid_api_key=' + ASSIGNED + '\n',
            'Tavern/.env.example': 'xybrid_api_key=\n',
            'Tavern/Assets/Scenes/demo.unity': '%YAML 1.1\nMonoBehaviour:\n  ' + OBSOLETE + ': xybrid_api_key\n',
            'Tavern/Assets/Scripts/Service.cs': 'DotEnv.Get("xybrid_api_key");\nvar k = "' + PREFIXED + '";\n',
            'Tavern/Plugins/native.bin': b'\0\0' + PREFIXED.encode(),
            'Tavern/Packages/inner.zip': inner.getvalue(),
            'Tavern/Packages/legacy.unitypackage': b'\x1f\x8b',
        })

        failed, output = self.run_check(path)

        self.assertTrue(failed)
        lines = set(output.splitlines())
        self.assertEqual(lines, {
            f'environment file: {path}!Tavern/.env',
            f'credential assignment: {path}!Tavern/.env:1',
            f'obsolete serialized telemetry field: {path}!Tavern/Assets/Scenes/demo.unity:3',
            f'secret key prefix: {path}!Tavern/Assets/Scripts/Service.cs:2',
            f'secret key prefix: {path}!Tavern/Packages/inner.zip!settings.json:1',
            f'credential assignment: {path}!Tavern/Packages/inner.zip!settings.json:1',
            f'nested archive not scanned (scan its contents separately): {path}!Tavern/Packages/legacy.unitypackage',
        })
        for secret in SECRETS:
            self.assertNotIn(secret, output)

    def test_clean_archive_passes(self):
        path = self.write_zip('clean.zip', {
            'Tavern/README.md': 'Set xybrid_api_key in your environment.\n',
            'Tavern/Assets/Scripts/Service.cs': 'TelemetryApiKey.Resolve(DotEnv.Get, out var source);\n',
            'Tavern/.env.example': 'xybrid_api_key=\n',
        })
        failed, output = self.run_check(path)
        self.assertFalse(failed)
        self.assertIn('Archive check passed: 3 text entries scanned', output)

    def test_unreadable_archive_fails_closed(self):
        path = self.tmp / 'broken.zip'
        path.write_bytes(b'not a zip')
        failed, output = self.run_check(path)
        self.assertTrue(failed)
        self.assertIn('unreadable archive (BadZipFile): ' + str(path), output)


if __name__ == '__main__':
    unittest.main()
