# Aurora 21 UltimateTeam server image (Linux, .NET 10). Build from ultimateteam/:
#   docker build -f deploy/Dockerfile -t aurora21/ultimateteam-server .
# One image runs both the server (`serve --hosted ...`) and the TLS redirector (`redirector ...`); see deploy/README.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY server/Directory.Build.props server/.editorconfig server/
COPY server/database-setup/ server/database-setup/
COPY server/src/ server/src/
RUN dotnet publish server/src/Aurora21.UltimateTeam.Server/Aurora21.UltimateTeam.Server.csproj -c Release -r linux-x64 -o /out

# The client pack the Aurora launcher's Install button downloads (/client/install/*): Start-Aurora21.ps1, the auth bridge,
# the maths bridge with the one Microsoft ucrtbase.dll it loads into FIFA (and its notice), the Windows server executable
# for its trust-patch command, and EA's original CA moduli it replaces. CLIENT_VERSION (CI passes sha-<7>) names the
# pack; a published version never changes.
FROM build AS client
ARG CLIENT_VERSION=local
RUN dotnet publish server/src/Aurora21.UltimateTeam.Server/Aurora21.UltimateTeam.Server.csproj -c Release -r win-x64 \
        --self-contained true -p:PublishSingleFile=true -o /pack/trust \
    && rm -f /pack/trust/*.pdb
COPY runtime-source/gos2015-original-modulus.bin runtime-source/gs2019-original-modulus.bin /pack/trust/
COPY Aurora21-LocalAuthBridge.exe Aurora21-MathBridge.exe client-pack/Start-Aurora21.ps1 client-pack/runtime.json \
    client-pack/THIRD-PARTY-NOTICES.txt /pack/
# The pinned Microsoft ucrtbase.dll (Windows SDK redist 10.0.26100.1742; the helper refuses any other file). It is never
# copied next to FIFA21.exe: Windows always loads its own UCRT there. Aurora21-MathBridge.exe loads this copy into the
# running FIFA by full path and points FIFA's maths imports at it, so every player computes the same maths.
COPY client-pack/ucrt/ucrtbase.dll /pack/ucrt/
RUN apt-get update && apt-get install -y --no-install-recommends zip && rm -rf /var/lib/apt/lists/* \
    && case "$CLIENT_VERSION" in (*[!A-Za-z0-9._-]*|"") echo "invalid CLIENT_VERSION" >&2; exit 1;; esac \
    && sed -i "s/@VERSION@/$CLIENT_VERSION/" /pack/runtime.json \
    && mkdir -p /client-install && cd /pack && zip -qrX "/client-install/aurora21-client-$CLIENT_VERSION.zip" . \
    && file="aurora21-client-$CLIENT_VERSION.zip" \
    && size=$(stat -c %s "/client-install/$file") && sha=$(sha256sum "/client-install/$file" | cut -d' ' -f1) \
    && printf '{"schema":1,"version":"%s","created":"%s","files":[{"name":"%s","size":%s,"sha256":"%s","kind":"runtime"}],"game":{"exe":"FIFA21.exe"},"notes":"Aurora 21 hosted play: runtime.json, Start-Aurora21.ps1, the auth bridge, the maths bridge and the trust patch"}\n' \
        "$CLIENT_VERSION" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$file" "$size" "$sha" > /client-install/manifest.json

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=build /out/ /app/
COPY --from=client /client-install/ /app/client-install/
# The bundled catalogs and rules (read-only). Certificates and state live in volumes, never in the image.
COPY runtime-source/ultimateteam21/ /app/runtime-source/ultimateteam21/
# Npgsql probes for the Kerberos library at start-up; without it every start logs a harmless load error.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/state /data/certs && chown -R app:app /data
# ASPNETCORE_HTTP_PORTS is cleared: the server binds its own listeners (the base image's default 8080 only adds a warning).
ENV ASPNETCORE_HTTP_PORTS= \
    ULTIMATETEAM_DATA_ROOT=/app/runtime-source/ultimateteam21 \
    ULTIMATETEAM_RUNTIME_ROOT=/data/state \
    ULTIMATETEAM21_USER_CONFIG_ROOT=/data/state/config \
    ULTIMATETEAM_CERTIFICATE_ROOT=/data/certs \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net
USER app
VOLUME ["/data/state", "/data/certs"]
ENTRYPOINT ["/app/Aurora21-UltimateTeam-Server"]
CMD ["serve", "--hosted", "--host", "0.0.0.0"]
