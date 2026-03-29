# Stage 1: Build with .NET 9 SDK
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY CryptoBotStockSharp.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app --no-restore

# Stage 2: Runtime with .NET 9
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app

# P2 Fix: Create directories with correct ownership for non-root user.
# .NET 9 containers run as non-root by default ($APP_UID).
ARG APP_UID=1654
RUN mkdir -p /app/logs /app/state && \
    chown -R ${APP_UID}:${APP_UID} /app/logs /app/state

COPY --from=build /app .

ENV TZ=UTC

ENTRYPOINT ["dotnet", "CryptoBotStockSharp.dll"]