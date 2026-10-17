# OpenD2 {tag} ({date})

Source commit: `{commit}` · Roadmap phase: {phase}

## Summary

{summary}

## Changes (from CHANGELOG.md)

{changes}

## Download and verify

Check every file against `SHA256SUMS` (`sha256sum -c SHA256SUMS`). `release-manifest.json` lists each package with
its size, SHA-256, commit and data-guard result. Two builds of the same commit produce the same hashes.

## Requirements

You need your own Diablo II 1.11b (or 1.14d) MPQ files. **No game data is included**: every package passed the
game-data guard (extensions, `data/global/` paths, Godot PCK contents{hash_note}).

## Known issues

- The NPC runtime (llama-server) is not bundled in local builds; its runtime test is a known failure.
{known_issues}

## QA

{qa}
