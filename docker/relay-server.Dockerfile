# Relay server: control plane + API.
# The single writer over Postgres; workers never touch the database.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY relay.slnx ./
COPY src/Relay.Core ./src/Relay.Core
COPY src/Relay.Server ./src/Relay.Server
RUN dotnet restore src/Relay.Server
RUN dotnet publish src/Relay.Server -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Relay.Server.dll"]
