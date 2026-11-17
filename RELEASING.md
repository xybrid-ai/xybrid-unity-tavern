# Source distribution

Distribute reviewed source with instructions for local licensed asset imports.
Run `python3 tools/check_repository.py` before publishing source.

## Credential scan

`tools/check_repository.py` rejects credential-shaped values in tracked and
trackable text files: known key prefixes (for example `sk_live_`, `xy_test_`,
`hf_`, `ghp_`), private key blocks, and literal values assigned to names such
as `api_key`, `secret` or `token`. A bare `xybrid_api_key` variable name and
placeholders such as `<your key>` pass. It also rejects the removed
`_telemetryApiKeyEnvVar` field in scenes, prefabs, assets and C# files.
Findings name the file, line and rule; matched values are never printed.

Before you upload any release ZIP, scan it explicitly:

```sh
python3 tools/check_repository.py --archive path/to/release.zip
```

The archive check reads every text entry, including entries in nested ZIPs,
without extracting the archive. It fails on `.env` entries (except
`.env.example`, `.env.sample` and `.env.template`), credential-shaped values,
the removed serialized field, unreadable or encrypted entries, and nested
archives that it cannot open (for example `.unitypackage`). Binary entries are
skipped, so it does not certify compiled player data. Repeat `--archive` to
scan several ZIPs. The scanner tests use synthetic fixtures only:
`python3 -m unittest discover -s tools/tests`.

Telemetry keys come from local configuration only (see the README): Editor
preferences or `.env` in the Editor, and the `xybrid_api_key` process
environment variable in player builds.

Do not run the legacy full-project release ZIP script or upload full `Assets/`
archives, paid pack files, embedded art meshes, models or native tool bundles.
The old `scripts/release.sh` is superseded by this policy and remains local.

Existing release ZIPs require an explicit content audit and, if needed, separate
authorized removal. They have not been inspected or removed by this split.

Player releases are separate from source distribution and require the relevant
asset/model licenses to be checked. No player build is part of this migration.
