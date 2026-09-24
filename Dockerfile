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
RUN dotnet publish src/Assistant.Host/Assistant.Host.csproj -c Release -o /app/publish --no-restore

FROM ${DOTNET_RUNTIME_IMAGE} AS final
WORKDIR /app
ARG GIT_SHA=dev
ARG BUILD_TIME=""
ENV GIT_SHA=${GIT_SHA}
ENV BUILD_TIME=${BUILD_TIME}
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["dotnet", "Assistant.Host.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Assistant.Host.dll"]
