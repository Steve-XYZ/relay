# Relay worker: claims jobs and drives sandboxes.
# Mounts /var/run/docker.sock so it can spawn sandbox containers on the host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY relay.slnx ./
COPY src/Relay.Core ./src/Relay.Core
COPY src/Relay.Worker ./src/Relay.Worker
RUN dotnet restore src/Relay.Worker
RUN dotnet publish src/Relay.Worker -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends docker.io git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Relay.Worker.dll"]
