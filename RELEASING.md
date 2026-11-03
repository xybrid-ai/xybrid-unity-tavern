# Source distribution

Distribute reviewed source with instructions for local licensed asset imports.
Run `python3 tools/check_repository.py` before publishing source.

Do not run the legacy full-project release ZIP script or upload full `Assets/`
archives, paid pack files, embedded art meshes, models or native tool bundles.
The old `scripts/release.sh` is superseded by this policy and remains local.

Existing release ZIPs require an explicit content audit and, if needed, separate
authorized removal. They have not been inspected or removed by this split.

Player releases are separate from source distribution and require the relevant
asset/model licenses to be checked. No player build is part of this migration.
