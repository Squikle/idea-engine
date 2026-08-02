# syntax=docker/dockerfile:1
# Multi-arch (amd64/arm64) image for the idea-engine worker.
# Build:  docker build -t idea-engine .
# The same Dockerfile serves Mac (dev), Raspberry Pi and VPS (arm64/amd64).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore layer: only project/build files, so dependency layers cache well.
COPY global.json Directory.Build.props ./
COPY src/IdeaEngine.Core/IdeaEngine.Core.csproj src/IdeaEngine.Core/
COPY src/IdeaEngine.Infrastructure/IdeaEngine.Infrastructure.csproj src/IdeaEngine.Infrastructure/
COPY src/IdeaEngine.Worker/IdeaEngine.Worker.csproj src/IdeaEngine.Worker/
RUN dotnet restore src/IdeaEngine.Worker/IdeaEngine.Worker.csproj

COPY src/ src/
RUN dotnet publish src/IdeaEngine.Worker/IdeaEngine.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app .

# Writable log dir for the non-root user (bind-mounted in compose).
RUN mkdir -p /app/logs && chown app:app /app/logs
USER app

ENTRYPOINT ["dotnet", "IdeaEngine.Worker.dll"]
