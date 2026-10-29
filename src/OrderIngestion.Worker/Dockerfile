# syntax=docker/dockerfile:1
# Build context is the repository root:
#   docker build -f src/OrderIngestion.Worker/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore using only the project files first, so the package layer stays cached
# until a .csproj changes.
COPY Directory.Build.props ./
COPY src/OrderIngestion.Domain/OrderIngestion.Domain.csproj src/OrderIngestion.Domain/
COPY src/OrderIngestion.Infrastructure/OrderIngestion.Infrastructure.csproj src/OrderIngestion.Infrastructure/
COPY src/OrderIngestion.Worker/OrderIngestion.Worker.csproj src/OrderIngestion.Worker/
RUN dotnet restore src/OrderIngestion.Worker/OrderIngestion.Worker.csproj

COPY src/ src/
RUN dotnet publish src/OrderIngestion.Worker/OrderIngestion.Worker.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

# The plain .NET runtime: the worker has no web server, so it doesn't need the ASP.NET image.
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app

# Folders for the SQLite file and log files. compose mounts volumes here; the non-root
# app user needs to own them to write.
RUN mkdir -p /app/data /app/logs && chown "$APP_UID" /app/data /app/logs

COPY --from=build /app .
USER $APP_UID

ENTRYPOINT ["dotnet", "OrderIngestion.Worker.dll"]
