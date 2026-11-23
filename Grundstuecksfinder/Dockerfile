FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

COPY ["Grundstuecksfinder/Grundstuecksfinder.csproj", "Grundstuecksfinder/"]

COPY . .
WORKDIR "/src/Grundstuecksfinder"
RUN dotnet publish "Grundstuecksfinder.csproj" -c Release -o /app/publish

# Generate third-party license list and place it in wwwroot so it is served as a static file at /licenses.json
RUN dotnet tool install --global dotnet-project-licenses \
    && export PATH="$PATH:/root/.dotnet/tools" \
    && dotnet-project-licenses \
        --input . \
        --output-format json \
        --output-filename /app/publish/wwwroot/licenses.json \
    || echo "License generation failed – skipping."

# The app's writable folders, made here because the final image has no shell to run mkdir.
RUN mkdir -p /writable/logs /writable/.aspnet/DataProtection-Keys

# Ubuntu "chiseled": only the runtime's own files, no shell, no package manager, no curl, so
# an attacker in the container finds no tools. "-extra" adds ICU and tzdata, which the page
# needs for German number formatting (LANG=de_DE.UTF-8) and local times (TZ=Europe/Berlin).
FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .

# Run as the image's unprivileged "app" user (APP_UID). The app only writes its log files
# (logs/ here), the DataProtection keys (under $HOME, a volume in docker-compose.yml) and
# downloads in /tmp. The keys directory exists in the image so that a new volume mounted on
# it takes over its owner.
COPY --from=build --chown=$APP_UID:$APP_UID /writable/logs /app/logs
COPY --from=build --chown=$APP_UID:$APP_UID /writable/.aspnet /home/app/.aspnet
USER $APP_UID

# Port 8080 is the image's default (ASPNETCORE_HTTP_PORTS); setting ASPNETCORE_URLS as well
# only made ASP.NET Core warn that it overrides it.
EXPOSE 8080

ENTRYPOINT ["dotnet", "Grundstuecksfinder.dll"]
