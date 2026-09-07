# Build from the repository root:  docker build -f src/AuctionPlatform.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone so the layer caches across source edits.
COPY AuctionPlatform.slnx ./
COPY src/AuctionPlatform.Domain/*.csproj src/AuctionPlatform.Domain/
COPY src/AuctionPlatform.Application/*.csproj src/AuctionPlatform.Application/
COPY src/AuctionPlatform.Infrastructure/*.csproj src/AuctionPlatform.Infrastructure/
COPY src/AuctionPlatform.Api/*.csproj src/AuctionPlatform.Api/
COPY tests/AuctionPlatform.Tests/*.csproj tests/AuctionPlatform.Tests/
RUN dotnet restore src/AuctionPlatform.Api/AuctionPlatform.Api.csproj

COPY src/ src/
RUN dotnet publish src/AuctionPlatform.Api/AuctionPlatform.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl backs the container healthcheck; the base image ships without an HTTP client.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# The image already provides a non-root "app" user (UID 1654). The API is stateless, so it needs
# nothing beyond write access to its own log directory.
RUN mkdir -p /app/logs && chown -R $APP_UID:$APP_UID /app/logs
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AuctionPlatform.Api.dll"]
