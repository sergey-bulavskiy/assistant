# syntax=docker/dockerfile:1
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/Assistant.Domain/Assistant.Domain.csproj src/Assistant.Domain/
COPY src/Assistant.Application/Assistant.Application.csproj src/Assistant.Application/
COPY src/Assistant.Infrastructure/Assistant.Infrastructure.csproj src/Assistant.Infrastructure/
COPY src/Assistant.Host/Assistant.Host.csproj src/Assistant.Host/
RUN dotnet restore src/Assistant.Host/Assistant.Host.csproj

COPY src/ src/
COPY roles/ roles/
RUN dotnet publish src/Assistant.Host/Assistant.Host.csproj -c Release -o /app/publish --no-restore

FROM ${DOTNET_RUNTIME_IMAGE} AS final
WORKDIR /app
ARG GIT_SHA=dev
ARG BUILD_TIME=""
ENV GIT_SHA=${GIT_SHA}
ENV BUILD_TIME=${BUILD_TIME}
ENV ASPNETCORE_URLS=http://+:8080
ENV HOME=/home/app
ENV CLAUDE_HOME=/home/app/.claude-home
ENV CODEX_HOME=/home/app/.codex

# curl/ca-certificates/tzdata ONLY (spec §8.10, licence decision C9): the Claude Code CLI is proprietary
# and this image is public, so it is never installed here -- ClaudeCliInstallerHostedService installs
# it at container startup into $CLAUDE_HOME (aspnet:10.0 does not ship either package by default).
# Both HOME and CLAUDE_HOME are created and chowned to $APP_UID here, while still root --
# $CLAUDE_HOME is later replaced by a named volume mount (docker-compose.yml), and Docker copies an
# existing image directory's ownership into a freshly created empty volume on first mount, so no
# separate init container/chown step is needed in compose.
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p ${HOME} ${CLAUDE_HOME} ${CODEX_HOME} \
    && chown -R $APP_UID:$APP_UID ${HOME} ${CLAUDE_HOME} ${CODEX_HOME}

# Apache-licensed Codex CLI is pinned and checksum-verified at build time. This image supports
# Linux amd64; changing the release/architecture requires a reviewed isolation contract.
RUN test "$(dpkg --print-architecture)" = amd64 \
    && curl --fail --silent --show-error --location \
       https://github.com/openai/codex/releases/download/rust-v0.160.1/codex-x86_64-unknown-linux-musl.tar.gz \
       --output /tmp/codex.tar.gz \
    && echo '9226581be592d18f7e7f740a352fdb63aa61e45e39f7eb9b09d3888c84bba33f  /tmp/codex.tar.gz' | sha256sum --check --status \
    && tar -xzf /tmp/codex.tar.gz -C /tmp codex-x86_64-unknown-linux-musl \
    && install -m 0755 /tmp/codex-x86_64-unknown-linux-musl /usr/local/bin/codex \
    && test "$(/usr/local/bin/codex --version)" = 'codex-cli 0.160.1' \
    && mkdir -p /usr/local/share/licenses/codex \
    && curl --fail --silent --show-error --location \
       https://raw.githubusercontent.com/openai/codex/rust-v0.160.1/LICENSE \
       --output /usr/local/share/licenses/codex/LICENSE \
    && curl --fail --silent --show-error --location \
       https://raw.githubusercontent.com/openai/codex/rust-v0.160.1/NOTICE \
       --output /usr/local/share/licenses/codex/NOTICE \
    && rm /tmp/codex.tar.gz /tmp/codex-x86_64-unknown-linux-musl

COPY --from=build /app/publish .

USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["dotnet", "Assistant.Host.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Assistant.Host.dll"]
