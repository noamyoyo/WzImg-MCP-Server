# WzImg MCP server, Linux build (Phase 2: bitmap tools included). Build from the repository root:
#   docker build -t wzimg-mcp:linux-phase2 .
#
# Images are decoded, encoded and scaled with SkiaSharp (MIT licence) and its "NoDependencies" native
# library, which is copied from the NuGet package into the app directory: the runtime image needs no
# extra apt packages (no libgdiplus; no system fontconfig or freetype packages, FreeType is compiled into libSkiaSharp).
#
# Run (read-only root, no capabilities, data mounted at /data). The image listens on localhost by
# default, which no other container can reach, so pass --host 0.0.0.0 to listen on the container's
# network interface:
#   docker run --rm --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges:true \
#     --memory 3g -u 1000:100 -v /path/to/wz-data:/data wzimg-mcp:linux-phase2 --host 0.0.0.0
#
# The server has no authentication of its own. Do not publish the port to an untrusted network and
# NEVER run it with --network host: with --host 0.0.0.0 that would expose an unauthenticated tool
# server (it can read and write everything under /data) on every interface of the host. Put it on a
# private Docker network behind the MCP hub. File access is limited to /data
# (WZIMG_ALLOWED_ROOT). Keep extracted trees in a sub-directory of /data (e.g. /data/v83): when the
# allowed root is set, edit backups are written to a Backups directory inside the extracted tree
# (/data/v83/Backups), which category listing and packing skip.
#
# WZIMG_PRE_BIG_BANG (default 1, set 0 to change): the target client is a pre Big Bang client, which cannot
# read DXT images, so MapleLib's automatic pixel format choice never returns DXT. import_png and
# set_canvas_bitmap with format=auto store lossless bgra8888; DXT and the 16 bit formats stay available on request.

# Base images are pinned by digest. Tag at the time of pinning: mcr.microsoft.com/dotnet/sdk:10.0 and
# mcr.microsoft.com/dotnet/aspnet:10.0. To update, pull the new tag and replace the digest.
FROM mcr.microsoft.com/dotnet/sdk@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY . .
# The runtime identifier keeps only the native libraries for this CPU (SkiaSharp ships Windows, macOS and
# every Linux flavour; without it the image would carry about 450 MB of unused native files).
RUN case "$(uname -m)" in x86_64) RID=linux-x64 ;; aarch64) RID=linux-arm64 ;; *) echo "unsupported CPU $(uname -m)" >&2; exit 1 ;; esac  && dotnet publish WzImgMCP/WzImgMCP.csproj -c Release -o /out -r "$RID" --self-contained false -p:LinuxBuild=true -p:UseAppHost=false
# Keep the licence texts of the image library next to the app (MIT licence text and the notices for the
# native components that are compiled into libSkiaSharp.so).
RUN mkdir -p /out/licenses/skiasharp  && cp /root/.nuget/packages/skiasharp/*/LICENSE.txt /out/licenses/skiasharp/LICENSE.txt  && cp /root/.nuget/packages/skiasharp.nativeassets.linux.nodependencies/*/THIRD-PARTY-NOTICES.txt /out/licenses/skiasharp/THIRD-PARTY-NOTICES.txt

FROM mcr.microsoft.com/dotnet/aspnet@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir /data && chown 1000:100 /data

# Memory: workstation GC (one heap; the server GC makes a heap per core and keeps far more memory around).
# DOTNET_GCConserveMemory=5 (range 0 to 9) makes the GC compact the fragmented large object heap more eagerly, where the big
# arrays of a parsed image end up, at some CPU cost; 5 is a middle value. UNMEASURED: the build environment of this change had
# no game data and no Docker, so the value is chosen by reasoning and has not been compared with other values. The memory guard
# (WZIMG_MEMORY_SOFT_LIMIT_MB, WZIMG_IDLE_TRIM_SECONDS) does the real work; if the setting shows no gain on real data, remove it.
# The cache limit, the scan limits and the memory guard are WZIMG_* settings (README-LINUX.md, "Memory and big categories").
ENV WZIMG_ALLOWED_ROOT=/data \
    WZIMG_PRE_BIG_BANG=1 \
    HOME=/tmp \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=5

USER 1000:100
VOLUME /data
EXPOSE 13339
ENTRYPOINT ["dotnet", "WzImgMCP.dll", "--http", "--port", "13339"]
