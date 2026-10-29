# syntax=docker/dockerfile:1
# Build context is the repository root:
#   docker build -f simulate/OrderIngestion.MockApi/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY simulate/OrderIngestion.MockApi/OrderIngestion.MockApi.csproj simulate/OrderIngestion.MockApi/
RUN dotnet restore simulate/OrderIngestion.MockApi/OrderIngestion.MockApi.csproj

# Includes simulate/responses, which the publish step copies to /app/responses.
COPY simulate/ simulate/
RUN dotnet publish simulate/OrderIngestion.MockApi/OrderIngestion.MockApi.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080

HEALTHCHECK --interval=5s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -q -O /dev/null http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "OrderIngestion.MockApi.dll"]
