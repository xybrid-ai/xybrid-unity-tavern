# NPC Tavern — Xybrid Unity Example

The multi-NPC Tavern remains its own project. The standalone gatekeeper/orc
encounter is maintained separately at https://github.com/xybrid-ai/orc-demo.

## Source and local assets

Use **Unity 6000.6.4f1** and the **Xybrid Unity SDK 0.16.0**. For now
`Packages/manifest.json` references a local checkout of the SDK
(`~/Developer/Github/xybrid-sdk/bindings/unity`), so other machines must
check the SDK out at that path or change the reference. NPC voices use Gradium
Phonon through the SDK's tts-zzz backend, which no released native ships yet:
`Assets/Xybrid/Plugins/macOS/libxybrid_bolt.dylib` is a local build, and
`Assets/Xybrid/Plugins/.xybrid-native-macos-version` must match the SDK
package version or the SDK replaces it with a stock native.

Git tracks reviewed Tavern scripts, NPC prompts, render settings, package
definitions and project settings. No Git LFS is used.

Purchased art, models, installed native tools and generated caches stay local.
This includes Synty packs and imported art outside `Assets/Synty`. The existing
`Assets/Scenes/demo_xybrid.unity` also stays local: it contains embedded meshes
and must be converted to asset references before it can be distributed as source.
Ignoring the original pack folder alone does not remove those meshes.

For an existing licensed project, keep using the local scenes. For another
checkout, restore manifest-listed local dependencies from your licensed copy:

```sh
python3 tools/local_assets.py --from-project "/path/to/licensed/Tavern"
python3 tools/local_assets.py
```

The manifest records exact paths and importer metadata; it does not grant a
license or download paid packs. A stock pack import may not restore customized
assets or the embedded Tavern scene. A fresh checkout therefore requires a
licensed working-project copy until a reference-only scene recipe is provided.

The original local orc files remain in this working project for continuity;
new orc work belongs in the standalone repository. Tavern files and the
standalone project's files are independent.

## Telemetry (optional)

Xybrid telemetry is off unless an API key is configured. Without one, models
load normally. The key is never stored in scenes, prefabs or assets.

- **Editor:** open **Unity > Settings > Xybrid Tavern > Telemetry** (Windows:
  **Edit > Preferences**), paste the key into the masked **API Key** field, or
  press **Clear** to remove it. The key is saved in Unity EditorPrefs on this
  machine, scoped to this project's folder, outside the project files.
  EditorPrefs are plain local configuration, not encrypted secret storage. A
  saved key takes precedence. Otherwise the Editor reads `xybrid_api_key` from
  the project-root `.env` (ignored by Git), then from the process environment.
- **Player builds:** set the `xybrid_api_key` environment variable for the
  player process. Builds never contain or read the Editor key.

The key takes effect the next time `XybridModelService` initializes, for
example when you enter Play mode. Telemetry log lines do not include the key,
configured values or SDK exception messages.

## Distribution

Run `python3 tools/check_repository.py` before committing source. It also
rejects credential-shaped values and the removed `_telemetryApiKeyEnvVar`
serialized field. Do not publish full Unity project ZIPs or raw paid assets.
Previous full-project release ZIPs are legacy artifacts and need a separate
content audit; this change does not remove or certify them. See `RELEASING.md`
for the current policy, including the release ZIP scan.

The repository license covers project-owned code, not purchased art, third-party
models, voice data or other local dependencies.
