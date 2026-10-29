# Linux build (fork)

This repository is a fork of [lastbattle/WzImg-MCP-Server](https://github.com/lastbattle/WzImg-MCP-Server)
(branched from upstream commit `96cfd6e`). The `MapleLib` submodule points at a fork of
[lastbattle/MapleLib](https://github.com/lastbattle/MapleLib) (branched from `22e32d13`), branch `linux-slim`.
The fork adds a Linux build that runs in a container. See [NOTICE](NOTICE) for attribution and licences.

Status: **Phase 2** (Phase 1 had no bitmap tools). The Windows build and the Windows test suite were **not run** for any of this work
(see "What was not verified" below).

## What the Linux build is

- A switch, `LinuxBuild`. It is on by default when the OS is not Windows (`Directory.Build.props`), and can be
  forced either way with `-p:LinuxBuild=true` or `-p:LinuxBuild=false`.
- With it on, the projects target plain `net10.0`, drop WinForms, WPF, SharpDX and the Windows-only NAudio
  packages, and use MonoGame DesktopGL and System.Drawing.Common instead. Windows-only code is guarded by the
  `LINUX_BUILD` compile constant.
- **The bitmap tools work.** MapleLib decodes and encodes canvas bitmaps with System.Drawing (GDI+) on Windows. On
  Linux there is no GDI+, so the Linux build uses MapleLib's own pixel decoders and encoders on raw BGRA32 buffers
  (`WzPngProperty.GetPixelsBgra32` / `SetPixelsBgra32`, `WzCanvasProperty.GetLinkedPixelsBgra32`) and SkiaSharp
  only for PNG/JPEG decoding, encoding and scaling. See "Images" below.
- Everything else works as before: reading and editing properties and strings, `extract_to_img`, `pack_to_wz`, and the
  sound tools (`import_sound` with MP3 reads the frame headers directly, because there is no ACM decoder on Linux).

## Images

Library (facts from the package metadata, not legal advice): **SkiaSharp 3.119.4** and
**SkiaSharp.NativeAssets.Linux.NoDependencies 3.119.4**. Both packages declare `<license type="expression">MIT</license>`,
authors "Microsoft", copyright "(c) Microsoft Corporation". The native package carries a `THIRD-PARTY-NOTICES.txt`
that has sections for the third-party components of the SkiaSharp native builds (skia, HarfBuzz, libpng, FreeType, libjpeg-turbo, libwebp, expat, zlib and others; `NOTICE` lists them all). Strings in `libSkiaSharp.so` show that FreeType, libpng 1.6.58, libjpeg-turbo 2.1.5.1 and libwebp are compiled into it. The Docker image keeps both texts in `/app/licenses/skiasharp/`. "NoDependencies" means the native library
needs no system fontconfig or freetype packages (FreeType is compiled into libSkiaSharp), so the runtime image needs **no extra apt packages** (checked: the image is
built from the unchanged `aspnet` base and the hardened container below decodes, scales and encodes images).
The Windows build does not use SkiaSharp; it keeps System.Drawing. The only server code that touches an image library
is `WzImgMCP/Imaging/CanvasImaging.cs` (`#if LINUX_BUILD`).

Tools:

| Tool | What it does on Linux |
| --- | --- |
| `view_canvas` (new) | Returns the canvas as an MCP **image content block** (`image/png`) plus a text block with the size, WZ pixel format, origin and a note when the image was scaled. `maxSize` (default and maximum 2048) caps the larger side: bigger images are scaled down (Skia linear filter) and the text says so. `_inlink` / `_outlink` are followed. Errors come back as `isError` results. |
| `export_png` | Writes the full-size PNG to a file; the output path must be inside `WZIMG_ALLOWED_ROOT`. |
| `export_all_images`, `batch_export_images` | PNG files (`batch_export_images` also writes JPEG with `format=jpg`: quality 90, 4:2:0, transparent areas blended on black). `format` must be `png`, `jpg` or `jpeg` (any case; `jpeg` is written as `.jpg`); anything else is refused. `format=jpeg` now writes real JPEG data (the file name is `.jpg`); before, only the exact spelling `jpg` did, and `jpeg` wrote PNG data into a file named `.jpeg`. |
| `get_canvas_bitmap`, `get_animation_frames` (`metadataOnly=false`), `resolve_canvas_link` | Base64 PNG in the text result, as on Windows, but capped: one canvas is omitted (with a "Truncated" note that points to `view_canvas` and `export_png`) when its base64 text would exceed `WZIMG_MAX_RESPONSE_BYTES` (8 MiB by default); `get_animation_frames` applies the limit to the total of all frames of one call and omits the frames that no longer fit. `view_canvas` scales the image down further when its PNG would exceed the limit (noisy images compress badly) and refuses only when even a tiny version does not fit. Use `view_canvas` to look at an image. |
| `validate_image` | Decodes every canvas to check it. |
| `import_png`, `set_canvas_bitmap` | Take the PNG as `base64Png` **or** `pngPath` (a file inside the allowed root); exactly one of the two. New optional `format`: `bgra8888` (exact), `bgra4444`, `bgra5551`, `bgr565`, `dxt3`, `dxt5`, or `auto`, which means `bgra8888`. Without `format`, `import_png` stores `bgra8888` and `set_canvas_bitmap` keeps the format the canvas already has (`bgra8888` when it has no pixels), so export, edit, import does not change the format. |

Limits on imported PNGs (all platforms; they are checked before anything is decoded): at most **4096 x 4096 pixels**
and **20 MiB (20,971,520 bytes)** of PNG data, read from the PNG header; the base64 text length is checked before it
is decoded; data without the PNG signature is refused; a header that disagrees with the decoder is refused. The
largest accepted import decodes to 64 MiB of pixels. Images read from WZ files keep MapleLib's own limits (32768
pixels per side, 256 MiB decoded). A refused call changes nothing. Paths go through `PathGuard`: a file outside
`WZIMG_ALLOWED_ROOT` (also through `..` or a symbolic link) is refused with "path outside allowed root".

WZ pixel formats (decode and encode are MapleLib's existing code, run on raw buffers; Format numbers are the WZ ones):

| Format | Pixels | Decoded to BGRA32 | Encoder (lossy?) |
| --- | --- | --- | --- |
| 1 | BGRA4444 | nibble x 17 | keeps the high nibble of every channel (lossy) |
| 2 | BGRA8888 | unchanged | exact, including colour under transparent pixels |
| 257 | ARGB1555 | channel c -> (c << 3) or (c >> 2); alpha bit -> 0 or 255 | 5 bit channels rounded to the nearest value, alpha = top bit of alpha (lossy) |
| 513 | RGB565 | as above, 6 bit green (g << 2) or (g >> 4), alpha 255 | 5/6 bit channels rounded to the nearest value, alpha dropped (lossy) |
| 517 | one RGB565 value per 16x16 block | block colour filled in | only used when it loses nothing (see below) |
| 3, 1026 | DXT3 | 4x4 blocks, 4 bit alpha | min/max colour box, 4 bit alpha (lossy) |
| 2050 | DXT5 | 4x4 blocks, interpolated alpha | min/max colour box, interpolated alpha (lossy) |
| 4098 | BC7 | decoded | not written by this code |

How this was verified: the unit tests (`CanvasPixelFormatTests`) build raw payloads by hand for every format and compare
with values worked out from the format definition (an independent reference DXT decoder is compared with
the real decoder on 1.1 million pixel images, including the SIMD path and partial edge blocks; BC7 with a hand-built mode 6 block). Encoders
are checked against the quantisation each format defines. On the real client files in the test set only Format 1, 2
and 513 occur (Morph.wz, Etc.wz: all 1; Ui.wz: 1, 2 and 513; every one of the 22,381 canvases decodes, and re-encoding
as BGRA8888 reproduces the pixels). Formats 257, 517, DXT3, DXT5 and BC7 were verified on synthetic data only.
The 16-bit expansion rule was taken from GDI+'s documented conversion and from MapleLib's own `RGB565ToColor`;
it was **not** compared against real GDI+ output (no GDI+ here).

What to know about the formats:

- `format=auto` (and no `format` for `import_png`) stores lossless `bgra8888`. MapleLib's own content based choice (4444,
  565, 1555, DXT) is never used by these two tools; ask for a lossy format explicitly if you want a smaller canvas.
  `set_canvas_bitmap` without `format` keeps the canvas's current format. The exact cases:
  - Format 1, 2, 257, 513, 2050 and 1026 keep their number. Format 3 (DXT3 "grayscale") and Format 1026 (DXT3) are two tags for
    the same pixel data; the tag of the canvas is kept whatever the new image looks like (it is not guessed from the pixels).
    With an explicit `format=dxt3` the encoder still chooses the tag from the pixels (Format 3 for grey images).
  - Format 517 (one RGB565 colour per 16x16 block) is kept only when the new image is a multiple of 16 wide and high and every
    16x16 block is one colour after quantisation; any other image is stored as Format 513 and the result carries a `warning`.
  - A DXT canvas (3, 1026, 2050) becomes `bgra8888` with a `warning` when the new image's size is not a multiple of four, and
    also when `WZIMG_PRE_BIG_BANG` is on (the default), because a pre Big Bang client cannot read DXT. `format=dxt3` / `dxt5`
    still stores DXT on request.
  - BC7 (4098) cannot be written and becomes `bgra8888` (with a `warning`). A canvas without pixels gets `bgra8888`.
- No drift: the 5 and 6 bit quantiser rounds to the nearest value (`round(v * 31 / 255)`, `round(v * 63 / 255)`), which
  inverts the decoders' bit replication. Decoding a 565, 1555 or 4444 canvas and encoding it again returns the same
  values (tested for all 65,536 16 bit values of each format), so export, edit, import cycles do not darken the image.
  Upstream MapleLib rounded down, which turned a 5 bit value of 1 into 8 and then back into 0. This also changes
  encoding on Windows and the RGB565 endpoints of DXT blocks.
- Upstream picked Format 517 (one pixel per 16x16 block) for every RGB565 image whose size is a multiple of 16, which
  destroys any image that is not flat per block. The fork now uses 517 only when each block is a single colour
  after quantisation, otherwise Format 513. This also changes the Windows build (see below).
- DXT3 and DXT5 need a width and height that are multiples of 4; `format=dxt3` / `dxt5` on other sizes is refused with
  a clear message. `format=auto` never picks DXT (see `WZIMG_PRE_BIG_BANG`).
- A canvas that has an `_inlink` / `_outlink` child is displayed and exported from the link target. `set_canvas_bitmap`
  writes the canvas's own pixel data, so such a canvas keeps showing the linked image until the link is removed; the result carries a `warning` that says so (the call still succeeds).
- PNG files written by Skia are not byte-identical to GDI+'s output, only pixel-identical. `view_canvas` scaling uses a
  linear/mipmap filter (the Windows branch uses bicubic).
- Code that still needs GDI+ and is not reachable from any MCP tool: the XML / PNG-folder serializers, the NX
  exporter, the XML deserializer and the Spine texture loader in MapleLib.

## Build and run with Docker

Build from the repository root (the submodule must be checked out):

```bash
docker build -t wzimg-mcp:linux-phase2 .
```

Run (this is the command from the `Dockerfile` header; see it for the reasoning):

```bash
docker run --rm --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges:true \
  --memory 3g -u 1000:100 -v /path/to/wz-data:/data wzimg-mcp:linux-phase2 --host 0.0.0.0
```

The server listens on port 13339 (streamable HTTP). Put the container on a private Docker network and reach
it from other containers by name. Do not add `-p` unless you bind it to `127.0.0.1` only
(for example `-p 127.0.0.1:13339:13339`).

The image is built for the CPU of the build host (`linux-x64` or `linux-arm64`): the Dockerfile publishes with that
runtime identifier so that only that CPU's native libraries (SkiaSharp, SDL, OpenAL) are copied.

Without Docker: `dotnet build WzImgMCP.sln -p:LinuxBuild=true`, then run `WzImgMCP.dll --http --port 13339`.
Tests: `dotnet test WzImgMCP.Tests -p:LinuxBuild=true`.

### Environment variables and options

| Name | Meaning |
| --- | --- |
| `WZIMG_ALLOWED_ROOT` | When set, every path argument of the file, batch and export tools is resolved (symlinks followed) and must lie inside this directory, otherwise the call fails with "path outside allowed root". Relative paths are taken relative to the root. The image sets it to `/data`. When unset, paths are not restricted. |
| `WZIMG_PRE_BIG_BANG` | `1` (default) or `0`. A v83 client is a pre Big Bang client and cannot read DXT images, so with `1` MapleLib's automatic format choice (`ImageFormatDetector`) never returns DXT3 or DXT5. `import_png` and `set_canvas_bitmap` do not use that automatic choice at all (`format=auto` means lossless `bgra8888`); the switch matters for code paths that still let MapleLib choose, and DXT stays available when you ask for it with `format=dxt3` / `dxt5`. Set `0` only for a post Big Bang client. |
| `WZIMG_MAX_RESPONSE_BYTES` | Upper limit for the image and audio data in one tool response (default `8388608`, 8 MiB): the base64 text of one canvas for `get_canvas_bitmap` and `resolve_canvas_link`, the total for `get_animation_frames(metadataOnly=false)`, the base64 size of the `view_canvas` PNG, and the base64 text of the sound for `get_sound_data` and `resolve_sound_link` (omitted with a "Truncated" note that points to `export_mp3`). A 4096 x 4096 noise canvas would otherwise be about 85 MB of text. Invalid or non-positive values keep the default. |
| `WZIMG_HOST` / `--host` | Address the HTTP transport binds to. The default is localhost. IPv6 literals are bracketed automatically. The image does **not** set it, so inside a container you must pass `--host 0.0.0.0` for other containers to reach the server. |
| `WZIMG_DEFAULT_DATA_PATH` | Folder (an extracted IMG tree with `manifest.json`) that is opened automatically by the first tool call that needs the data source, so no `init_data_source` is needed after a restart. Default `/data/img`. The path must pass `WZIMG_ALLOWED_ROOT`. If the folder is missing, has no `manifest.json` or is outside the root, nothing is opened and the tools answer "No data source initialized. Call init_data_source first." (the next call tries again). `init_data_source` still works and always wins: after it the default folder is never opened again. `get_data_source_info` shows `auto_initialized: true` or `false`. Two simultaneous first calls open the folder once. |
| `WZIMG_SESSION_IDLE_SECONDS` | How long an HTTP MCP session may be idle (no request in flight) before the server closes it; the next request with that session id then gets `404 Session not found`. Default `604800` (7 days), accepted range `10` to `604800`; anything else (text, decimals, out of range) falls back to the default and logs a warning at startup. The effective values are logged at startup. See "HTTP sessions" below. |
| `WZIMG_CACHE_MB` | Size limit of the image cache in MB (default `400`, accepted `16` to `65536`; anything else keeps the default). It is the same setting as `HaCreatorConfig` `Cache.MaxMemoryCacheMB`, which the server fills from this variable. Since the size estimate was calibrated (see "Memory and big categories") the figure approximates the real managed memory the cached images hold. |
| `WZIMG_SCAN_MAX_IMAGES` | How many images a scan may cover (default `150`): `search_by_name`, `search_by_value`, `find_broken_uols`, `batch_search` and `get_category_summary` with `detailed=true`. A larger scope is refused with `scope_too_wide` before any image is opened. |
| `WZIMG_SCAN_BUDGET_SECONDS` | Time budget of a scan in seconds (default `20`, `0` = none). When it ends, the matches found so far come back with `truncated: true`. |
| `WZIMG_BULK_MAX_IMAGES` | Sanity cap for the tools that work through a whole category on purpose, `preload_category` and `batch_export_images` (default `2000`). Above it they refuse with `scope_too_wide` unless `force=true`. |
| `WZIMG_MEMORY_SOFT_LIMIT_MB` | Managed heap size (`GC.GetTotalMemory(false)`) above which the memory guard trims (default `1536`, `0` = never for size). |
| `WZIMG_IDLE_TRIM_SECONDS` | Seconds without any tool call after which the memory guard trims once (default `300`, `0` = never when idle). |
| `WZIMG_TEST_WZ_PATH` | Tests only: a directory containing `String.wz` (GMS key, version 83). The image tests on real canvases also use `Morph.wz`, `Ui.wz` and `Etc.wz` from the same directory when they are there. Tests that need real data skip when it is not set. |

### HTTP sessions

`ModelContextProtocol.AspNetCore 0.1.0-preview.12` has **no stateless mode**: `HttpServerTransportOptions` has only
`ConfigureSessionOptions`, `RunSessionHandler`, `IdleTimeout`, `MaxIdleSessionCount` and `TimeProvider`. Every client
therefore needs a session (`Mcp-Session-Id`), kept in memory, and a session is lost when the server restarts or when it
has been idle for `IdleTimeout`. Facts from the library code:

- Any request resets the idle timer, when the request ends. A session with a request still running (or an open GET stream)
  is never closed as idle.
- The library checks every 5 seconds, so a session closes up to 5 seconds later than its timeout.
- The library compares `TimeProvider.GetTimestamp()` (nanoseconds on Linux) with `IdleTimeout.Ticks` (100 ns units) without
  converting, so a plain `IdleTimeout` of 2 hours (the library default) only lasts 72 seconds on Linux. The server scales the
  value by the timestamp frequency (`SessionIdleSettings.ToLibraryTimeout`), so `WZIMG_SESSION_IDLE_SECONDS` is real seconds.
- `MaxIdleSessionCount` is set to 100000 (the library default). Above that number the oldest idle sessions are closed even if
  they are younger than the timeout.

### save_image and the image cache

An image remembers the full path it was loaded from (`ImgFileSystemManager` keeps it in a `ConditionalWeakTable`).
`save_image` writes to exactly that file, whatever spelling of the name is used in the call (`map0/x.img`, `Map0/x`,
`Map0/../Map0/x.img`). The cache key is built from that same full path, so all those spellings share one cached image.
If that file or its folder has been removed while the image was loaded, `save_image` fails with an error and writes nothing
(no file or folder is recreated); the same happens for a symbolic link in the path, a read-only folder or any other failed
write (`success: false`, the file on disk unchanged). After a successful save every cache entry of the image is dropped, so
the next access reads the file that was written. Images with unsaved edits are never evicted from the cache (the cache may
grow past its size limit) and survive `clear_cache`; `save_image` or `discard_changes` ends that.

### Memory and big categories

A category such as `Map` holds thousands of images (5,622 images, 628 MB of files in the data this was measured on). Before this
change `list_images_in_category Map` opened and parsed every one of them to read their names: 60 s on a cold server (MCP clients
give up after 30 s), 400,000 characters of answer and 1.6 GiB of memory (measured, `--memory 3g`). Now:

| Tool | Behaviour |
| --- | --- |
| `list_images_in_category` | Names only, nothing is opened or parsed. Sorted by ordinal name. `limit` (default 200, maximum 1000) and `offset` (default 0); the answer has `total`, `returned`, `offset`, `limit`, `has_more` (printed only when true, like every flag in the text results) and `next_offset` (absent on the last page). `full_path` is now the real path inside the category (`Map/Obj/x.img`), where it used to be `category/file name`. The same paging applies to `subdirectory`. |
| `get_category_summary` | Opens nothing: image count, total file size, subdirectories, and one page (`limit`, `offset`, as above) of images with `size_bytes`. `detailed=true` brings back the property, canvas and sound counts per image; it opens the images of the page, so it goes through the scan guard. |
| `search_by_name`, `search_by_value`, `find_broken_uols`, `batch_search` | Scan guard (below). `category` is required unless `image` is given. |
| `get_statistics`, `get_parsed_images`, `unload_category` | Work on the category index and on what is in the cache; they no longer load every image to count or list them. `get_parsed_images` therefore lists the images in the cache that are parsed (it used to load, and so list, every image). `unload_category` also drops the images from the cache. |
| `preload_category` | Refused above `WZIMG_BULK_MAX_IMAGES` images unless `force=true`. Stops when the cache is 95% full (`stopped_at_cache_limit`), because going on only pushes out what was loaded a moment ago. |
| `batch_export_images` | Same cap and `force`. The images are read one after the other and not kept in the cache. |
| `get_cache_stats` | Also reports `pinned_image_count`, `cache_limit_bytes`, `managed_heap_bytes`, `process_working_set_bytes`, `trim_count`, `last_trim_utc`, `last_trim_reason` and `last_trim_released_images`. |

**Scan guard.** One helper (`ScanGuard`) is used by every tool that goes through many images. It first counts the images from the
category index; if there are more than `WZIMG_SCAN_MAX_IMAGES` the call fails with `error_code: scope_too_wide` and a message that
says how many images the category has and suggests `image` or a paged `list_images_in_category`. Otherwise the images are scanned
one after the other under the time budget (`WZIMG_SCAN_BUDGET_SECONDS`; at least one image is always scanned). The result carries
`scanned_images` and `total_images`, and `truncated: true` with `truncated_reason` (`max_results` or `time_budget`) when it is partial.
A category given without an image is `category_required`. An image the scan has to load is read, parsed, searched and **disposed**:
it never enters the cache, so a scan leaves the cache and its hit and miss counters as they were. An image that is already cached
(this includes every image with unsaved edits, which a scan therefore sees as edited) is used as it is and not touched.

**Memory guard.** A timer looks every 30 s at the managed heap and at the time of the last tool call. When the heap is above
`WZIMG_MEMORY_SOFT_LIMIT_MB` (at most once a minute), or no tool call happened for `WZIMG_IDLE_TRIM_SECONDS` (once per quiet
period), it drops every cached image without unsaved edits and runs a blocking, compacting collection that asks the runtime to
give memory back (`GCCollectionMode.Aggressive`, LOH compaction). Images with unsaved edits are never touched. It takes no lock
of the session and two ticks never overlap. Each trim logs one line with counts only, for example
`[MemoryGuard] trim (idle): released 860 cached image(s), kept 0 with unsaved edits; managed heap 377 MB -> 2 MB`. A tool call that
is cut by a trim just reloads the image from disk the next time; the one residual is an edit that is made in the instants between
an image being fetched from the cache and its `Changed` flag being set while a soft limit trim runs (the same window an ordinary
cache eviction has always had).

**The size estimate.** The cache evicts when the sum of `ImgFileSystemManager.EstimateWzImageSize` passes the limit. That estimate
used 100 bytes per property and was 2.0x to 4.8x below the real managed heap (measured over 13 categories), so a "512 MB" cache held
1.1 GB and more, and evicted images lingered until the next full collection. It now uses the measured cost of each kind of property
(see the comment above the constants in `ImgFileSystemManager.cs`). Measured with `GC.GetTotalMemory(true)` after loading real images
(heap growth divided by the estimate, 1.00 = exact): Map 0.96 (300 and 1,000 images), Mob 0.97, Character 0.98, Item 0.98, Skill 0.95,
Ui 0.97, String 1.01, Reactor 1.06, Npc 1.13. The factor before: Map 2.1, Mob 3.6, Character 3.4, Item 3.0, Skill 2.9, Npc 4.8.
The estimate counts the parsed property tree; payloads that are read later (a canvas bitmap, a sound) are counted only while they
are held, so the guard above looks at the real heap as well.

The Dockerfile sets `DOTNET_gcServer=0` (workstation GC: one heap, not one per core). `DOTNET_GCConserveMemory` was tried at 5, 7 and 9
and changed nothing measurable (see the comment in the Dockerfile), so it is not set.

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
10. **`WzFile.SaveToDisk` temp file.** The scratch file (a random `.<guid>.tmp` name, created with `CreateNew`) is now written next to the output file instead of
    in the process working directory, and is read back through the stream that wrote it.
11. **Case-insensitive category lookup** (`ImgFileSystemManager`). Category names given in any case are mapped to
    the real directory name on disk. This only changes behaviour on case-sensitive file systems.

Phase 2 additions (these also apply to a Windows build and have not been tested on Windows):

12. **Server: image import limits and new parameters.** `import_png` and `set_canvas_bitmap` take `base64Png` or the
    new `pngPath`, plus the new `format`; `base64Png` is no longer required. The 4096 x 4096 / 20 MiB limits and the
    path guard apply on Windows too. On Windows the decoding is still System.Drawing (with `format=auto`, the
    same `PNG` setter as before). The export tools write the bytes of `Bitmap.Save(MemoryStream)` to the file
    instead of letting `Save` open the file itself. `view_canvas` is a new tool.
13. **Server:** the Phase 1 `PlatformSupport` class (the "not available on the Linux build" errors) was removed.
14. **MapleLib: raw pixel API** (new members only) and the shared `BgraSurface` the encoders read from (a `Bitmap`
    is still locked exactly as before on Windows).
15. **MapleLib: Format 517** is only written when every 16x16 block is one colour (see "Images"). This changes which
    format `Bitmap`-based edits choose on Windows for opaque images whose size is a multiple of 16.
16. **Server: `batch_export_images` file names.** The file name of a canvas inside an image used the path of its
    parent, so all frames of an animation (`walk/0`, `walk/1`, ...) were written to the same `walk.png` and overwrote
    each other. The canvas name is now part of the file name (`walk_0.png`, `walk_1.png`).
17. **MapleLib: 5/6 bit quantiser** (`PngUtility.QuantizeTo5` / `QuantizeTo6`) rounds to the nearest value instead of down
    (see "Images"). This changes the bytes written for 565 and 1555 canvases and the colour endpoints of DXT blocks on
    Windows too.
18. **Server: `import_png` / `set_canvas_bitmap` `format=auto`** stores `bgra8888` instead of letting MapleLib choose a
    lossy format, on Windows too (the Windows branch no longer uses the `PNG` setter). `set_canvas_bitmap` without
    `format` keeps the canvas's current format.

    Fixes after the independent review of Phase 2 (all of them also apply to a Windows build and have not been tested there):

19. **Server: output paths of the bulk export tools.** `batch_export_images` accepts only `png`, `jpg` and `jpeg` as `format`
    (any case; `jpeg` is written with the extension `.jpg`) and a `category` that is one plain name (no separator, `..`, drive
    prefix, rooted path or control character). Canvas and image names are sanitised into one file name and made unique
    (`name`, `name_2`, ...). Every final file path of `batch_export_images`, `export_all_images` and `export_all_sounds`
    goes through `PathGuard.Resolve` (symlinks followed) before a directory is created or a file written, and a path outside
    the root fails the whole call. Without `WZIMG_ALLOWED_ROOT` the format and name checks still apply.
20. **Server: `WZIMG_PRE_BIG_BANG`** (default `1`) sets `ImageFormatDetector.UsePreBigBangImageFormats` at startup, so
    MapleLib's automatic format choice no longer returns DXT.
21. **Server: response size cap** (`WZIMG_MAX_RESPONSE_BYTES`, default 8 MiB): `get_canvas_bitmap`, `resolve_canvas_link`,
    `get_animation_frames` and `view_canvas` omit or shrink image data above it. `set_canvas_bitmap` on an `_inlink` /
    `_outlink` canvas returns a `warning`, and a refused `set_canvas_bitmap` no longer leaves an empty pixel property behind.
22. **MapleLib: a link that leads to a canvas without pixel data** gives no image from `GetLinkedPixelsBgra32` (as
    `GetLinkedWzCanvasBitmap` on Windows) instead of falling back to the linking canvas's own pixels, and the
    `ListWzUsed` setter works on the Linux build.

    Fixes after the second independent review (all of them also apply to a Windows build and have not been tested there):

23. **Server and MapleLib: `pack_to_wz` / `extract_to_img` categories.** Each entry of the `category` / `categories` list must be one plain
    name (no separator, `..`, drive prefix, rooted path or control character), also without `WZIMG_ALLOWED_ROOT`. Before, a category such
    as `../img/Map` put the packer output (`Map.wz`, `Data/Map`) outside the allowed root. `pack_to_wz` also resolves every file and
    directory the packer writes (`Category.wz`, `Category`, `Data/Category`, `Data`, `Data.wz`, `List.wz`) through `PathGuard.Resolve` before
    anything is created, and `WzPackingService.PackCategoryAsync` refuses a non-plain category itself. Symbolic links planted inside the
    output tree (`Data/<category>`, `Data/<category>/_Canvas` and the files in them) and the writes themselves are item 29.
24. **Server: `PathGuard.WriteFileGuarded` and the single-file exports** (`export_png`, `export_mp3`, `export_to_md`) write to a new file next
    to the target and rename it over the target, and refuse a symbolic link at the target, so a link planted between the path check and
    the write is never followed.
25. **Server: export names.** `batch_export_images` keeps image directory names unique within a category (`a/x.img` and `b/x.img`);
    name sets compare ignoring case; sanitised names lose trailing dots and spaces, get a `_` prefix when they are a Windows reserved device
    name (`CON`, `NUL`, `COM1` ...) and are cut to 150 UTF-8 bytes (not characters) at a character boundary.
26. **Server, MapleLib: `set_canvas_bitmap` without `format`** keeps Format 3 / Format 1026 exactly, reports in `warning` when Format 517 had to
    become 513, and stores `bgra8888` for a DXT canvas while `WZIMG_PRE_BIG_BANG` is on (see "Images"). `WzPngProperty.SetPixelsBgra32` has a new
    optional `dxt3Grayscale` argument.
27. **Server: `get_sound_data` and `resolve_sound_link`** omit the base64 audio data (with a `Truncated` note) above `WZIMG_MAX_RESPONSE_BYTES`.
28. **Server: `export_to_md`** changes the extension to `.md` before the path guard resolves the path.

    Fix after the third review of the packer:

29. **Server and MapleLib: the packer and the extractor do not write through a symbolic link.** MapleLib's `NoFollowFile` helper writes each
    output file (`Category.wz`, `Data.wz`, `List.wz`, `Category_NNN.wz`, `List_000.wz`, `Category.ini`, `Canvas.ini`, `_Canvas_NNN.wz`, the extracted `.img` files,
    `List.json`, `manifest.json` and the category index) to a new file with a random name next to the target and renames it over the target; a symbolic link at the target (also a
    dangling one) is refused with an error, never followed. Before `pack_to_wz` creates anything, `PathGuard.Resolve` also checks
    `Data/<category>` and `Data/<category>/_Canvas`, and every symbolic link found inside them, so a link that leads outside the allowed
    root is refused with a message that names the path (`BatchTools.GuardPackOutputs`). This is a check of the state at that moment; the
    write itself stays safe even if a link appears afterwards. Residuals that are not fixed:
    - a symbolic link swapped into a **parent** directory of the output after the check is still followed (closing that needs `openat` on
      the directory);
    - the rename gives the output file the default permissions and owner of a new file, not those of the file it replaced;
    - a crash between the create and the rename can leave a `.<guid>.tmp` file in the output directory;
    - the audio size cap (`WZIMG_MAX_RESPONSE_BYTES`) applies after `GetBytes` has loaded the sound, so a very large sound is read into memory
      before it is cut off.
    - on Windows the rename (`MoveFileEx` with replace) fails on a read-only target or one held open without delete sharing; not tested there;
    - a link to a place **inside** the root is not refused by `GuardPackOutputs`; the packer's own write refuses it later, so files written
      before that point (for example `Category.ini`) can remain.
    - Old behaviour that was left alone: `pack_to_wz` with `saveAs64Bit` reports a failure although it wrote the files (it counts only
      top-level `*.wz` files).

    Fix after the fourth review (links in directories of the tree, not only at the written file):

30. **MapleLib: `NoFollowFile.RefuseSymbolicLinksBelow`** refuses a symbolic link (or reparse point), also a dangling one, at the root or at
    any existing directory or file on the way down to a path. `ImgFileSystemManager.SaveImageToFile` (`save_image`) uses it for the image
    path (below the data source) and for the backup path (below the `Backups` directory), so a link planted in a category directory, in
    `Backups` or at the image file makes the save fail and nothing is written or copied outside the tree; the temporary file is created
    with `CreateNew`. The extractor resolves its `List` output directory like a category directory (`List.json` is no longer written through
    a `List` link). `WzFile.SaveToDisk` reads its scratch file back through the stream that wrote it instead of reopening it by name, and
    the temporary files are closed when a writer cannot be created. `List.json` was never written before: the extractor serialised an
    anonymous object with the source-generated `ListWzJsonFormat` serializer and threw `InvalidCastException` (an upstream fault from the
    System.Text.Json migration); it now builds the real `ListWzJsonFormat`. Before this fix every extraction that had `List.wz` entries
    ended with `Success = false` after the images were written; a failing `List.json` write still fails the extraction (the error is
    reported, the extracted images stay). The path prefix tests in `ImgFileSystemManager`, `VirtualWzDirectory` and in the new helper are exact on Linux and ignore
    case only on Windows, so `/data/V83` is not taken for `/data/v83`. All of these are checks of the state at that moment; a link
    swapped in between the check and the write is the residual described in item 29.

    Effects to know (Windows, not tested): the check also refuses directories that carry a reparse point that is not a link, for
    example a OneDrive Files On-Demand folder. Also: a data source directory that is itself a symbolic link, or a `Backups` directory that is a link (for example to
    another disk), makes every `save_image` fail with the generic "not changed" error (the reason is only in the debug output). Mount the
    real directory instead of linking to it. The backup file name is chosen before the image is serialised, so two saves of the same
    image within the same second can collide on the backup name and the second fails (nothing is lost).

    Memory and big categories (all of these also apply to a Windows build and have not been tested there; see "Memory and big categories"):

31. **MapleLib: `IDataSource.GetImagePathsInCategory`** is a new interface member (names of all images of a category, sorted, nothing
    opened), implemented by the IMG, WZ and hybrid sources. An implementation outside this repository has to add it. New members
    that are not part of the interface: `ImgFileSystemManager.OpenImageUncached`, `GetImageFileSize`, `GetCachedImages`,
    `ReleaseUnpinnedImages`, `PinnedImageCount`, `LRUCache.TryPeek` and `CountPinned`; `LRUCache.ClearUnpinned` now returns the number
    of items it removed (it returned nothing).
32. **MapleLib: the size estimate of the image cache** (`ImgFileSystemManager.EstimateWzImageSize`, now public) is about 2x to 5x higher
    than before, because it now counts what an image really occupies. The same `MaxMemoryCacheMB` therefore keeps fewer images than
    it did, and the limit is no longer far below the real memory. `DataSourceStats` has two new fields, `CacheLimitBytes` and `PinnedImageCount`.
33. **Server: tool results.** `list_images_in_category` is paged (200 by default) and its `full_path` is the real path of nested images;
    `search_by_name` and `search_by_value` without `image` need a `category` and refuse a category above `WZIMG_SCAN_MAX_IMAGES`
    images; `find_broken_uols` and `batch_search` are limited the same way (`batch_search` with its default `all` therefore fails on a
    big data set); scan results name an image by its path in the category; `get_category_summary` no longer counts properties unless
    `detailed=true` and lists one page; `get_parsed_images` lists only cached images; `unload_category` leaves images that are not
    cached alone and drops the others from the cache. `Result<T>` and the result types of the changed tools have an `error_code`.

Path audit of the tools: the only tool code that joins a user supplied string into a path with `Path.Combine` is in `BatchTools`
(`batch_export_images` category: checked as a plain name; image directory and canvas file names: sanitised, unique, and written through
`PathGuard`; `pack_to_wz` / `extract_to_img` categories: see 23). Every other tool passes its path argument through `PathGuard.Resolve`
and does not join strings. Reads of images by category and image name go through MapleLib's `ImgFileSystemManager`, which checks the
combined path against the version directory (lexically, symbolic links inside the data directory are not followed or checked there).

## What was not verified

- **The Windows build and the Windows test suite were not run.** All building and testing was done in the Linux
  container (`dotnet/sdk:10.0`).
- Windows image code: the Windows branches of `CanvasImaging` (System.Drawing) were only **compiled** (the file was
  built without `LINUX_BUILD` against System.Drawing.Common), never run; the Windows build of the whole solution could
  not be built here. The changes to MapleLib's encoders keep the same `LockBits` call on Windows but were not run
  there.
- Tests on Linux: 464 tests (159 before the first review fixes, 268 before the second, 357 before the third, 394 before the fourth, 404 before the fifth step, 410 before the session, auto-open and save_image changes, 464 now). Measured with `WZIMG_TEST_WZ_PATH` pointing at String.wz and Morph.wz only: 459 pass, 5 skip; without the variable 438 pass, 26 skip. The figures in the next sentences are from the earlier four-file run:
  with `WZIMG_TEST_WZ_PATH` pointing at String.wz, Morph.wz, Ui.wz and Etc.wz,
  405 pass, 0 fail, 3 skip (the three upstream `IntegrationTests` that need a Windows data path). With only String.wz
  and Morph.wz 403 pass and 5 skip; without the variable 382 pass and 26 skip. Tests that need symbolic links are reported as
  skipped (not passed) where links cannot be created. All 29 upstream tests that failed in
  Phase 1 (they share `TestFixture`, which created a `System.Drawing.Bitmap`) now pass, because the fixture uses the raw
  pixel API under `LINUX_BUILD`. Two Phase 1 tests (`PlatformSupportTests`) were deleted with the stub they tested.
  The upstream test `ModifyToolsTests.CanvasAndPersistenceOps_Work` accepts an error from `set_canvas_bitmap`, and its
  1x1 PNG has wrong checksums (bad IDAT CRC, zlib stream without Adler-32) that libpng rejects, so that test no longer
  proves the image path; `CanvasImagingTests` covers it with a valid PNG and a strict assertion.
- Tests after the memory and big category work: 514 tests; without `WZIMG_TEST_WZ_PATH` 487 pass and 27 skip (474 tests, 447 pass and 27 skip before it). Not run
  with the real WZ data variable. The estimate calibration and the container figures above were measured on a real 2 GB extracted v83 tree
  (not part of the tests, which build their own small trees).
- The packer's multi-IV fallback and the Lua IV preference were checked with `String.wz` (GMS) and mixed-key
  trees; the Lua preference has not been exercised against a real Lua data set.
