# Unity CLI & Unity MCP

Two ways to drive Unity without clicking: a headless CLI wrapper for
compile/test loops, and Unity's official MCP server so agents can read the
console and touch the scene graph while the Editor is open.

## Unity CLI — `scripts/unity.sh`

Resolves the editor path from `ProjectSettings/ProjectVersion.txt`, so it
follows the project when the version bumps.

| Command | What it does |
|---|---|
| `./scripts/unity.sh version` | Project editor version + install status |
| `./scripts/unity.sh install` | Install the matching editor via Unity Hub (headless) |
| `./scripts/unity.sh open` | Launch the Editor on this project |
| `./scripts/unity.sh compile` | Headless recompile, non-zero exit on compiler errors |
| `./scripts/unity.sh tests editmode` | Run tests, NUnit XML to `Logs/test-results-editmode.xml` |
| `./scripts/unity.sh method UnityCli.Refresh` | Run any static editor method |
| `./scripts/unity.sh log` | Tail the live Editor log |
| `./scripts/unity.sh errors` | Grep the newest batch log for `error CS`/exceptions |

Batch-mode commands need the Editor **closed** — Unity takes an exclusive lock
(`Temp/UnityLockfile`) and the script refuses to run rather than corrupting the
Library folder.

Batch entry points live in `Assets/Scripts/Editor/UnityCli.cs`.

## Unity MCP

Unity's first-party MCP server ships inside `com.unity.ai.assistant`
(`2.19.0-pre.2`, added to `Packages/manifest.json`). The Editor installs a relay
binary to `~/.unity/relay/` on first launch; `.mcp.json` points Claude Code at
it over stdio.

**Unlike the CLI, MCP requires the Editor to be open and running** — the relay
proxies into the live Editor process.

Tools exposed: scene hierarchy and GameObject management, component
inspection/modification, C# script creation, console log reading, build and
platform settings.

Setup steps are in the Unity Editor handoff (see git log / session notes):
Project Settings ▸ AI ▸ Unity MCP Server, confirm **Unity Bridge: Running**,
then approve the pending connection from Claude Code.

Unity's AI tools beta may require an active trial or subscription on the Unity
account signed into the Editor.
