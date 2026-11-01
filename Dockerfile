FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/Assistant/Assistant.csproj src/Assistant/
RUN dotnet restore src/Assistant/Assistant.csproj
COPY src/Assistant/ src/Assistant/
RUN dotnet publish src/Assistant/Assistant.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Assistant.dll"]
