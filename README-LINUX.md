# Linux build (fork)

This repository is a fork of [lastbattle/WzImg-MCP-Server](https://github.com/lastbattle/WzImg-MCP-Server)
(branched from upstream commit `96cfd6e`). The `MapleLib` submodule points at a fork of
[lastbattle/MapleLib](https://github.com/lastbattle/MapleLib) (branched from `22e32d13`), branch `linux-slim`.
The fork adds a Linux build that runs in a container. See [NOTICE](NOTICE) for attribution and licences.

Status: **Phase 1**. The Windows build and the Windows test suite were **not run** for any of this work
(see "What was not verified" below).

## What the Linux build is

- A switch, `LinuxBuild`. It is on by default when the OS is not Windows (`Directory.Build.props`), and can be
  forced either way with `-p:LinuxBuild=true` or `-p:LinuxBuild=false`.
- With it on, the projects target plain `net10.0`, drop WinForms, WPF, SharpDX and the Windows-only NAudio
  packages, and use MonoGame DesktopGL and System.Drawing.Common instead. Windows-only code is guarded by the
  `LINUX_BUILD` compile constant.
- **No bitmap tools yet.** MapleLib decodes and encodes canvas bitmaps with System.Drawing (GDI+), which is not
  present on Linux. These tools return a structured "not available on the Linux build yet" error:
  `get_canvas_bitmap`, `get_animation_frames` (with `metadataOnly=false`), `validate_image`, `export_png`,
  `export_all_images`, `batch_export_images`, `set_canvas_bitmap`, `import_png`. `resolve_canvas_link` keeps its
  link information and reports the missing image data in `image_note`.
- What does work: reading and editing properties and strings, `extract_to_img`, `pack_to_wz`, sound tools
  (`import_sound` with MP3 reads the frame headers directly, because there is no ACM decoder on Linux), and
  everything else that only needs the compressed canvas data.

## Build and run with Docker

Build from the repository root (the submodule must be checked out):

```bash
docker build -t wzimg-mcp:linux-test .
```

Run (this is the command from the `Dockerfile` header; see it for the reasoning):

```bash
docker run --rm --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges:true \
  --memory 3g -u 1000:100 -v /path/to/wz-data:/data wzimg-mcp:linux-test --host 0.0.0.0
```

The server listens on port 13339 (streamable HTTP). Put the container on a private Docker network and reach
it from other containers by name. Do not add `-p` unless you bind it to `127.0.0.1` only
(for example `-p 127.0.0.1:13339:13339`).

Without Docker: `dotnet build WzImgMCP.sln -p:LinuxBuild=true`, then run `WzImgMCP.dll --http --port 13339`.
Tests: `dotnet test WzImgMCP.Tests -p:LinuxBuild=true`.

### Environment variables and options

| Name | Meaning |
| --- | --- |
| `WZIMG_ALLOWED_ROOT` | When set, every path argument of the file, batch and export tools is resolved (symlinks followed) and must lie inside this directory, otherwise the call fails with "path outside allowed root". Relative paths are taken relative to the root. The image sets it to `/data`. When unset, paths are not restricted. |
| `WZIMG_HOST` / `--host` | Address the HTTP transport binds to. The default is localhost. IPv6 literals are bracketed automatically. The image does **not** set it, so inside a container you must pass `--host 0.0.0.0` for other containers to reach the server. |
| `WZIMG_TEST_WZ_PATH` | Tests only: a directory containing `String.wz` (GMS key, version 83). Tests that need real data skip when it is not set. |

## Security notes

- **`PathGuard` (`WZIMG_ALLOWED_ROOT`) is not a chroot or a sandbox.** It checks the path arguments the tools
  receive. The real boundary is the container: the read-only root file system, the dropped capabilities, and
  above all which host directory you mount at `/data`. Mount only a copy of the data you are willing to have
  changed.
- **The server has no authentication.** Anyone who can reach the port can read and write everything under
  `/data`. Never publish the port to an untrusted network, never expose it directly to the internet, and
  **never run it with `--network host`** together with `--host 0.0.0.0`: that would expose an unauthenticated
  tool server on every interface of the host.
- `scan_img_directories` does not follow symbolic links, and checks each directory and `manifest.json` against
  the allowed root.

## Licence facts (not legal advice)

These are the facts found in the files, not an interpretation:

- This repository's `LICENSE` is the MIT licence, "Copyright (c) 2026 lastbattle".
- `MapleLib/LICENSE` contains the full text of the **GNU General Public License, version 3**. The MapleLib
  `README.md` says "MIT License - See LICENSE file for details", which does not match the `LICENSE` file in the
  same repository. The fork did not change either file.
- `MapleLib/spine-runtimes-2.1.25/LICENSE` is the "Spine Runtimes Software License Version 2.2" (Esoteric
  Software).
- The Docker image is built from this repository including the MapleLib submodule, so it contains MapleLib code
  compiled in. If you distribute the image or the binaries, check the licence terms yourself first.

## Behaviour changes that are not behind `LINUX_BUILD`

Most of the fork is guarded by `LINUX_BUILD` or only active when `WZIMG_ALLOWED_ROOT` is set. The following
changes are **not** guarded and therefore also apply to a Windows build. They have not been tested on Windows.

Server (`WzImgMCP`):

1. **Changed flag.** Every edit tool (`set_*`, `add_property`, `delete_property`, `rename_property`,
   `copy_property`, `import_*`, `set_canvas_bitmap`) now sets `Image.Changed` after a successful edit. Before,
   the flag stayed unset, so `save_image` and `pack_to_wz` copied the original bytes and the edit was lost.
2. **`discard_changes`** clears `Changed` and forces a reload from disk. It reports an error if the reload
   fails. Before, it left the image empty and a following `save_image` wrote a roughly 13-byte file.
3. **`save_image`** now returns the failure when the write fails (it used to report success), and drops the
   image from the cache after a successful save so the next access reads the file that was written.
4. **`unparse_image` and `unload_category`** are refused while an image has unsaved edits
   ("unsaved changes: call save_image or discard_changes first").
5. **`scan_img_directories`** no longer follows symbolic links or junctions (`FileAttributes.ReparsePoint`
   is skipped), with or without `WZIMG_ALLOWED_ROOT`.
6. **`--host` / `WZIMG_HOST`** is a new option. The default (localhost) is unchanged.

MapleLib (`linux-slim`):

7. **Serializer re-serialises on key mismatch** (`WzImgSerializer`). An unchanged image whose source key
   differs from the output key is now re-serialised from its parsed properties instead of having its original
   bytes copied. This changes the bytes written by `extract_to_img` for such images, so extracted trees really
   are BMS-encrypted as the manifest says.
8. **Packer IV fallback and ordering** (`WzPackingService`). When packing, each `.img` is tried against a list of
   candidate IVs until one parses: BMS first, then the pack target IV, then GMS, then EMS (duplicates removed).
   Before, only BMS was used and any other key failed. The packer therefore relies on a wrong key failing to
   parse; this was not tested against deliberately ambiguous data. **Lua exception:** legacy binary Lua images
   (`.lua.img`) try the pack target IV first, because they were written with the pack target key; every
   other image keeps the order above.
9. **Backups location.** With `WZIMG_ALLOWED_ROOT` set, the `Backups` directory is created inside the version
   directory (for example `/data/v83/Backups`), not next to it. Category listing and packing skip it. Without
   the variable the location is unchanged (a sibling directory).
10. **`WzFile.SaveToDisk` temp file.** The `.TEMP` scratch file is now written next to the output file instead of
    in the process working directory.
11. **Case-insensitive category lookup** (`ImgFileSystemManager`). Category names given in any case are mapped to
    the real directory name on disk. This only changes behaviour on case-sensitive file systems.

## What was not verified

- **The Windows build and the Windows test suite were not run.** All building and testing was done in the Linux
  container (`dotnet/sdk:10.0`).
- Of the 76 tests, 29 fail on Linux with and without the fork changes: they share a test fixture
  (`TestFixture`) that creates a `System.Drawing.Bitmap`, which needs `gdiplus.dll`. That is a limit of the
  upstream tests on Linux, not a regression. The GDI+-free tests added by the fork (extraction, edit safety,
  pack round trip, path guard, scan, backup location) pass when `WZIMG_TEST_WZ_PATH` points at a directory with
  `String.wz`.
- The packer's multi-IV fallback and the Lua IV preference were checked with `String.wz` (GMS) and mixed-key
  trees; the Lua preference has not been exercised against a real Lua data set.
