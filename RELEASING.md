# Releasing

## Creating a New Release

1. Run the release script with a version number:
   ```bash
   ./scripts/release.sh v0.1.0-alpha3
   ```

2. This creates a zip in `releases/` containing the full Unity project

3. Create a GitHub release and upload the zip:
   ```bash
   git tag v0.1.0-alpha3 && git push origin v0.1.0-alpha3
   gh release create v0.1.0-alpha3 releases/Night-Tavern-v0.1.0-alpha3.zip \
     --repo xybrid-ai/xybrid-unity-tavern \
     --title "Night-Tavern v0.1.0-alpha3"
   ```
