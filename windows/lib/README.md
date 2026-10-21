# libsodium.dll

The official libsodium 1.0.22 build for Windows x64, unchanged:
`libsodium/x64/Release/v143/dynamic/libsodium.dll` from `libsodium-1.0.22-msvc.zip`
(<https://download.libsodium.org/libsodium/releases/>, 17,690,194 bytes, SHA-256
`3e03a726fac4bc09cb61d8f29d658ef7a5eca0811de59082130414f7ca2e4279`), whose minisign
signature checked out against libsodium's published key
`RWQf6LRCGA9i53mlYecO4IzT51TGPpvWucNSCh1CBM0QTaLn73Y7GFO3`.

- `libsodium.dll`: 462,848 bytes, SHA-256
  `f656aeb789bfc3a2ac587fae1d7cfe278bbe8ce73f28da73b4d08282ea8f9fe3`.
- License: ISC (`LICENSE-libsodium.txt`).
- `scripts/build.ps1` embeds it in `T7Drop.exe`, which writes it out at its first start
  (`src/Sodium.cs`) and checks the SHA-256 above before loading it.
- It needs the Visual C++ runtime (`VCRUNTIME140.dll`), which most Windows installations
  have; the app says what to install when it is missing. The mingw build of the same
  release needs `libgcc_s_*.dll`, which its package does not include.
