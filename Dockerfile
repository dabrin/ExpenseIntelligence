# syntax=docker/dockerfile:1

# ---- 1) Angular build (emits into src/ExpenseIntelligence.Api/wwwroot, see web/angular.json)
FROM node:20-alpine AS web
WORKDIR /repo/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npx ng build

# ---- 2) .NET publish (wwwroot from the previous stage is bundled)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /repo
COPY ExpenseIntelligence.sln ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore src/ExpenseIntelligence.Api/ExpenseIntelligence.Api.csproj
COPY --from=web /repo/src/ExpenseIntelligence.Api/wwwroot/ src/ExpenseIntelligence.Api/wwwroot/
RUN dotnet publish src/ExpenseIntelligence.Api/ExpenseIntelligence.Api.csproj -c Release -o /out --no-restore

# ---- 3) Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=api /out ./
COPY data/original/ /app/seed/
# Live data (working copy, audit, uploaded files) must live on a persistent volume mounted at /data.
ENV Storage__SeedPath=/app/seed/gastos_desordenados.json \
    Storage__WorkingDirectory=/data \
    ASPNETCORE_ENVIRONMENT=Production
VOLUME /data
EXPOSE 8080
# appsettings.json pins "Urls" to localhost:5080, so override that key (not ASPNETCORE_URLS) and honour the platform's $PORT.
CMD ["sh", "-c", "exec env Urls=http://0.0.0.0:${PORT:-8080} dotnet ExpenseIntelligence.Api.dll"]
