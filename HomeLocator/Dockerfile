FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["HomeLocator/HomeLocator.csproj", "HomeLocator/"]
RUN dotnet restore "HomeLocator/HomeLocator.csproj"

COPY . .
WORKDIR "/src/HomeLocator"
RUN dotnet publish "HomeLocator.csproj" -c Release -o /app/publish --no-restore

# Generate third-party license list and place it in wwwroot so it is served as a static file at /licenses.json
RUN dotnet tool install --global dotnet-project-licenses \
    && export PATH="$PATH:/root/.dotnet/tools" \
    && dotnet-project-licenses \
        --input . \
        --output-format json \
        --output-filename /app/publish/wwwroot/licenses.json \
    || echo "License generation failed – skipping."

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "HomeLocator.dll"]
