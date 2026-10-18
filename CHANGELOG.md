# Changelog

All notable changes to OpenD2 are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).
The single version source is the root `VERSION` file; see [release testing](docs/migration/RELEASE_QA.md#versioning).

Every PR adds its user-visible change under `[Unreleased]`. When a version is cut, rename the heading to
`[X.Y.Z] - YYYY-MM-DD` and set `VERSION` to the same number.

Version map: 0.1.0 = P1 original data + P2-1 foundation, 0.2.0 = P2, 0.3.0 = P3, 0.4.0 = P4,
0.5.0-0.9.x = P5, 1.0.0 = behaves like Diablo II 1.14d. Before 1.0, breaking save or configuration changes may ship
in a MINOR release but must be listed here.

## [Unreleased]

### Added

- Per-screen and per-act palette rules (auto palette), PL2 light (0-31) and 25/50/75/additive/multiplicative blend in
  the original-mode render path, `--gamma` ramp; asset cache PNG palette chosen per resource instead of fixed act1
  (P2-2, partially complete: the lighting MAE/SSIM comparison against original captures moves to P2-2b).

### Changed
- Data guard (#125 follow-up): the offset media search fails closed when it hits its candidate cap (C-speed prefilters
  keep decoys from reaching it), decompressed tars are checked member by member plus any bytes after the end of the
  archive (an "empty tar" of zero blocks is scanned as plain data), WAV hits need a real fmt/data chunk, and DC6
  candidates are checked without copying (32 MB of 0xCD: 17.4 s → 0.2 s).
- Asset cache: permission-denied resources or palettes are recorded in `errors` and the build continues.
- `eng/verify.py` treats CR, U+0085, U+2028 and U+2029 as line ends like C#.
- `eng/verify.py`: preprocessor directive lines can no longer open a comment/string (`#region /*`), and `.cs` files in
  nested `bin`/`obj` folders (which the SDK compiles) are checked; only the project's own `bin`/`obj` are skipped.
- Data guard: WAV/Ogg/Bink/DC6/MPQ headers are also found after a prefix (each candidate structurally checked, e.g.
  Ogg page CRC), and every gzip/bz2/xz member is scanned on its own.
- P2-2 follow-up: independent palette reference script `eng/palette_reference.py` with reference hashes for all 18
  original palettes; the asset cache reports PNGs skipped because their palette could not be read.
- Release tooling: `eng/verify.py` checks code inside interpolated-string holes and `"Godot.*"` type-name strings,
  `\uXXXX` identifier escapes and sub-folders; the data guard validates the MPQ manifest shape, bounds RSCC block size,
  scans bytes after streams inside RSCC blocks, after short high-ratio raw deflate and after bz2/xz streams, rejects an
  explicitly configured asset cache that does not exist, and Linux packages built on Windows mark every ELF/`#!` file 0755.

## [0.1.0] - Unreleased

First versioned release. Versioning restarted here; the earlier `v0.2.0-rc.1`-`rc.4` preview tags were removed and
their synthetic-preview and online work (through #102) is the baseline this version builds on.

### Added

- Root `VERSION` file as the single version source for MSBuild (`VersionPrefix`, `InformationalVersion`), Godot
  `config/version` (`eng/version.py`) and release tags; `eng/release.py check-version` accepts stable tags and fails
  when the tag differs from `VERSION`.
- Release game-data guard (`eng/data_guard.py`): extensions, `data/global/` paths, run-time original hashes, nested
  archives and Godot PCK contents; `eng/release.py pack` refuses a payload that fails it.
- Local release script `eng/release-local.py` (no CI) with reproducible archives, `SHA256SUMS`, manifest and
  `docs/release/NOTES_TEMPLATE.md`; this changelog.
- P1 MPQ migration pipeline: `D2_DATA_PATH`, per-version MPQ SHA-256 manifest, extraction/PNG cache outside the
  repository, cache-aware asset reads, synthetic and genuine-data tests (#108).
- P1 resource validation across genuine 1.11b data, with DS1/COF/DC6/TXT shape fixes it found (#109).
- PL2 lighting palettes and font TBL fields, validated against genuine 1.11b (#111).
- Semantic Excel table schemas, BIN consistency checks and the M4-01 content inventory with raw and playable
  counts (#112).
- P2-1 original-mode 800x600/640x480 frame buffer with integer letterbox, deterministic headless capture and
  ScreenDiff (match %, MAE, SSIM) (#113).
- P2-1 follow-ups: shared sharp prescale as the default, guarded `--original-mode` options, box-filtered downscale
  below 1x, fit-based 1920x1080 acceptance and a real Godot window vs CPU ScreenDiff test (#115).
- Tighter GPU window diff (content rect, black bars, per-channel error, nearest tie-flip model), 1920x1080 cases,
  RGB hash pins and ScreenDiff `--screen`/`--scale` (#116).

### Fixed

- Preview loaders are `async Task` and their faults are observed at the call sites (#103).
- `PersonalHost` implements `IAsyncDisposable`; synchronous `Dispose` stays off the caller's context (#104).
- Room stream shutdown cause is logged at debug level (#105); CodeRabbit follow-up for it (#106).
- Follow-ups: restored `PersonalHost.DisposeAsync`, capped post-kill wait, accurate stream close cause, observed UI
  tasks, and the master build broken by #106 (#107).
- Corpus validation: no DT1 reclassification when lvltypes names no tilesets, accurate Skipped wording, trailing
  separator accepted in `--include` (#114).

### Security

- Asset cache rejects trailing dot/space and reserved device names (COM0/LPT0/CONIN$/CONOUT$) and caps total PNG
  output (#110).
- Game data is only read from the owner's install and cached outside the repository (#108, #110).

[Unreleased]: https://github.com/imagineiluv-star/OpenD2/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/imagineiluv-star/OpenD2/commits/v0.1.0
