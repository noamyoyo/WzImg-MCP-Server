# WzImg MCP server, Linux build (Phase 1: no bitmap tools). Build from the repository root:
#   docker build -t wzimg-mcp:linux-test .
#
# Run (read-only root, no capabilities, data mounted at /data):
#   docker run --rm --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges:true \
#     --memory 3g -u 1000:100 -v /path/to/wz-data:/data wzimg-mcp:linux-test
#
# The server has no authentication of its own. Do not publish the port to an untrusted network;
# put it behind the MCP hub / a private Docker network. File access is limited to /data
# (WZIMG_ALLOWED_ROOT). Keep extracted trees in a sub-directory of /data (e.g. /data/v83): when the
# allowed root is set, edit backups are written to a Backups directory inside the extracted tree
# (/data/v83/Backups), which category listing and packing skip.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish WzImgMCP/WzImgMCP.csproj -c Release -o /out -p:LinuxBuild=true -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir /data && chown 1000:100 /data

ENV WZIMG_HOST=0.0.0.0 \
    WZIMG_ALLOWED_ROOT=/data \
    HOME=/tmp \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_EnableDiagnostics=0

USER 1000:100
VOLUME /data
EXPOSE 13339
ENTRYPOINT ["dotnet", "WzImgMCP.dll", "--http", "--port", "13339"]
