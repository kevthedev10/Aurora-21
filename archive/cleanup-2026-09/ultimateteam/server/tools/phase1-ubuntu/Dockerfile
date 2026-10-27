FROM mcr.microsoft.com/dotnet/sdk:10.0.100-noble

ARG DEBIAN_FRONTEND=noninteractive
ARG POSTGRES_MAJOR=18

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl postgresql-common \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl --fail --show-error --silent \
        --output /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
        https://www.postgresql.org/media/keys/ACCC4CF8.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt noble-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install --yes --no-install-recommends "postgresql-${POSTGRES_MAJOR}" \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /opt/aurora21-postgres/linux-x64 /work \
    && ln -s "/usr/lib/postgresql/${POSTGRES_MAJOR}/bin" /opt/aurora21-postgres/linux-x64/bin \
    && useradd --create-home --uid 10001 aurora21 \
    && chown aurora21:aurora21 /work

ENV ULTIMATETEAM_POSTGRES_ROOT=/opt/aurora21-postgres \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

USER aurora21
WORKDIR /work
