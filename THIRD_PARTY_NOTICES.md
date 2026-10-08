# Migration dependency provenance

OpenD2's existing LICENSE continues to apply to its own implementation.

StormLib 9.30.0 is built from commit `44ebfbfc109d76e2a85bbd5d8b0c949df7e65c6f` of https://github.com/ladislav-zezula/StormLib.git (MIT, copyright Ladislav Zezula). Its complete source, including bundled compression/crypto code and original notices, is fetched by `eng/build-native.py`; no native binary is committed. The exact dependency is recorded in `eng/dependencies.lock.json`.

The bundled code includes zlib, bzip2, PKLIB, LZMA, Huffman, ADPCM, libtommath and libtomcrypt. Preserve their upstream file-level copyright/license notices when redistributing or modifying the dependency. `StormLib-source-notices.txt` accompanies builds; the pinned source repository remains the complete source/provenance reference. This build has no dependency on a Blizzard DLL.

The narrow C ABI in `native/mpq.cpp` is OpenD2-owned source. Rebuild with CMake 3.25+ and a C++17 compiler using `python eng/build-native.py`. Windows uses static MSVC runtime, macOS builds universal arm64/x86_64, Linux uses the host C/C++ runtime. Bit-identical builds across compilers have not been established.

Game MPQs and extracted assets are user-supplied, read-only data; they are not included in this repository or CI artifacts. Godot and .NET provenance remains documented in `docs/migration/M0_DEPENDENCIES.md`.

M1-05 DCC/COF implementation reorganizes the existing GPL-3.0 OpenD2 format algorithms in `Engine/DCC.cpp`, `DCC.hpp` and `COF.hpp` into C#. Preserve the original credits to Necrolis, SVR, Paul Siramy and eezstreet. OpenDiablo2/dcc (`16ddc7029d0bf7a90e59f22ace61a17d604722cc`) and OpenDiablo2/cof (`180eb64494ba142c2b2bb6281f74c4fb1275bfa8`) were consulted for comparison; no external Go source, game testdata or additional codec package is bundled.

M1-06 DT1/DS1 parsers reorganize the existing OpenD2 `Engine/DT1.cpp/.hpp` and `Engine/DS1.cpp/.hpp` format handling into C#. Preserve credits to Paul Siramy and eezstreet. Comparison sources: OpenDiablo2/dt1 (`de3bbef08e138514cfc4862670f581d6ea10f0bf`), OpenDiablo2/ds1 (`8224aff0172b089d78c65a6b7466b948a46ffb73`), and eezstreet/DT1-Tools (`ed2f15fa27913669ee6de21a993b3e5f5bd1a7ff`). No external source, game assets, or additional runtime dependency is copied or bundled.

M1-07 map table layouts and the four-byte BIN record-count prefix were checked against OpenD2 `Modcode/Common/DataTables.cpp`, `Shared/D2DataTables.hpp`, and ThePhrozenKeep/D2MOO commit `5596f5cb6c5251a0a07c6637d26458b06099d516` (`source/D2Common/src/DataTbls/LevelsTbls.cpp`, `DataTbls.cpp`, and `include/DataTbls/LevelsTbls.h`). The C# bounded parsers, map projection and tile cache are new OpenD2 source. No external source, original DLL, game table or runtime dependency is bundled.

M2-01 `SimulationRandom` preserves the uint32 xorshift transition from OpenD2 `Modcode/Common/D2Common_Math.cpp` (eezstreet). The C# bounded range conversion, scheduling, simulation state, replay and inspector are OpenD2 source. The legacy 16-bit truncation and unsafe integer seed cast are not carried over. No new runtime dependency or original game resource is introduced.


## NPC-02 optional local inference

llama.cpp v0.6.0 is built from the annotated tag's peeled commit `d81235049384534c167caea52b85a694f6103d14` of https://github.com/ggml-org/llama.cpp.git (MIT). `eng/build-npc.py` checks the exact source commit and clean worktree, builds `llama-server` from source, and ships its LICENSE plus dependency notices (including cpp-httplib, json.hpp and hash helpers). The immutable source commit retains full file-level notices. The CPU-only executable is bundled as build output, never committed. Shell subprocess tools, web UI and automatic model fetching are disabled; OpenD2 owns the bounded C# adapter and model installer. Linux uses system C/C++ libraries; Windows statically links the MSVC runtime. Builds are reproducible from source, not certified bit-identical across compilers.

Optional weights are official Qwen GGUF files under Apache-2.0. The complete license is preserved in `eng/notices/Qwen-Apache-2.0.txt` and copied into the distributed NPC runtime directory. The models are unmodified downloads with repository/revision/file/size/SHA-256 pinned in `NpcModelCatalog.cs`; they are not committed, included in CI artifacts, or automatically downloaded. Source attribution:

- Qwen/Qwen3-0.6B-GGUF, revision `1208e45d782fe18602c5eaf10e5758d5b0f24c03`, `Qwen3-0.6B-Q4_K_M.gguf`, 396704416 bytes, SHA-256 `b0638f08417a2d3c8652760462eb5407c6e30173cf9608ad0820757a281eea0e`. This older official immutable revision is intentional; current main no longer lists this Q4 file.
- Qwen/Qwen3-1.7B-GGUF, revision `90862c4b9d2787eaed51d12237eafdfe7c5f6077`, `Qwen3-1.7B-Q8_0.gguf`, 1834426016 bytes, SHA-256 `061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a`.

License source: https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/blob/1208e45d782fe18602c5eaf10e5758d5b0f24c03/LICENSE. Model cards/files: https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/tree/1208e45d782fe18602c5eaf10e5758d5b0f24c03 and https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/tree/90862c4b9d2787eaed51d12237eafdfe7c5f6077. No Blizzard model or game data is introduced.
