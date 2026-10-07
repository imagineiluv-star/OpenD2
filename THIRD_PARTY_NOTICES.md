# M1 MPQ dependency provenance

OpenD2's existing LICENSE continues to apply to its own implementation.

StormLib 9.30.0 is built from commit `44ebfbfc109d76e2a85bbd5d8b0c949df7e65c6f` of https://github.com/ladislav-zezula/StormLib.git (MIT, copyright Ladislav Zezula). Its complete source, including bundled compression/crypto code and original notices, is fetched by `eng/build-native.py`; no native binary is committed. The exact dependency is recorded in `eng/dependencies.lock.json`.

The bundled code includes zlib, bzip2, PKLIB, LZMA, Huffman, ADPCM, libtommath and libtomcrypt. Preserve their upstream file-level copyright/license notices when redistributing or modifying the dependency. `StormLib-source-notices.txt` accompanies builds; the pinned source repository remains the complete source/provenance reference. This build has no dependency on a Blizzard DLL.

The narrow C ABI in `native/mpq.cpp` is OpenD2-owned source. Rebuild with CMake 3.25+ and a C++17 compiler using `python eng/build-native.py`. Windows uses static MSVC runtime, macOS builds universal arm64/x86_64, Linux uses the host C/C++ runtime. Bit-identical builds across compilers have not been established.

Game MPQs and extracted assets are user-supplied, read-only data; they are not included in this repository or CI artifacts. Godot and .NET provenance remains documented in `docs/migration/M0_DEPENDENCIES.md`.

M1-05 DCC/COF implementation reorganizes the existing GPL-3.0 OpenD2 format algorithms in `Engine/DCC.cpp`, `DCC.hpp` and `COF.hpp` into C#. Preserve the original credits to Necrolis, SVR, Paul Siramy and eezstreet. OpenDiablo2/dcc (`16ddc7029d0bf7a90e59f22ace61a17d604722cc`) and OpenDiablo2/cof (`180eb64494ba142c2b2bb6281f74c4fb1275bfa8`) were consulted for comparison; no external Go source, game testdata or additional codec package is bundled.
