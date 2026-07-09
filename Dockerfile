# Multi-stage build producing a single image: the ASP.NET Core API serving the
# built React SPA from wwwroot. The same image runs the CLI ingestion mode
# (args are appended to the entrypoint):
#   docker compose run --rm api ingest backfill

# --- Stage 1: frontend build ---
FROM node:22-alpine AS web-build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# --- Stage 2: API publish ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ResearchDiscovery.Domain/ResearchDiscovery.Domain.csproj src/ResearchDiscovery.Domain/
COPY src/ResearchDiscovery.Application/ResearchDiscovery.Application.csproj src/ResearchDiscovery.Application/
COPY src/ResearchDiscovery.Infrastructure/ResearchDiscovery.Infrastructure.csproj src/ResearchDiscovery.Infrastructure/
COPY src/ResearchDiscovery.Api/ResearchDiscovery.Api.csproj src/ResearchDiscovery.Api/
RUN dotnet restore src/ResearchDiscovery.Api/ResearchDiscovery.Api.csproj
COPY src/ src/
RUN dotnet publish src/ResearchDiscovery.Api/ResearchDiscovery.Api.csproj -c Release -o /app/publish --no-restore

# EF migration bundle: a standalone migrations applier shipped inside the
# image so deploys run schema migrations as a one-off container job
# (./efbundle --connection "..."), never at app startup in production.
RUN dotnet tool install dotnet-ef --tool-path /tools --version "10.*"
RUN /tools/dotnet-ef migrations bundle \
      --project src/ResearchDiscovery.Infrastructure \
      --startup-project src/ResearchDiscovery.Api \
      --configuration Release \
      --output /app/efbundle

# --- Stage 3: runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=api-build /app/publish ./
COPY --from=api-build /app/efbundle ./efbundle
COPY --from=web-build /web/dist ./wwwroot/
# The local embedding model downloads on first use into ./models; that is the
# only path the app writes, so it is the only one the non-root user owns.
RUN mkdir -p /app/models && chown -R app:app /app/models
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ResearchDiscovery.Api.dll"]
