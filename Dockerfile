# =========================================================================================
# Production image for the Sentinel portal.
#
# Three stages: the front-end bundles are built with Node, the application with the .NET SDK,
# and only the published output lands in the runtime image — so neither Node, npm, the SDK,
# nor any source file ships to production.
# =========================================================================================

# ------------------------------------------------------------------ front-end bundles ----
FROM node:24-alpine AS frontend
WORKDIR /frontend

# Copied first so the dependency layer is reused whenever only source files change.
COPY src/Sentinel.Web/package.json src/Sentinel.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY src/Sentinel.Web/vite.config.js ./
COPY src/Sentinel.Web/Scripts ./Scripts
RUN npm run build

# ------------------------------------------------------------------------- .NET build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the manifests alone, so a code-only change does not re-download packages.
COPY Directory.Build.props Directory.Packages.props NuGet.config ./
COPY src/Sentinel.Domain/Sentinel.Domain.csproj src/Sentinel.Domain/
COPY src/Sentinel.Application/Sentinel.Application.csproj src/Sentinel.Application/
COPY src/Sentinel.Infrastructure/Sentinel.Infrastructure.csproj src/Sentinel.Infrastructure/
COPY src/Sentinel.Web/Sentinel.Web.csproj src/Sentinel.Web/
RUN dotnet restore src/Sentinel.Web/Sentinel.Web.csproj

COPY src/ src/

# Overwrite any committed bundles with the ones just built from source.
COPY --from=frontend /frontend/wwwroot/js/dist/ src/Sentinel.Web/wwwroot/js/dist/

RUN dotnet publish src/Sentinel.Web/Sentinel.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ----------------------------------------------------------------------------- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The Debian-based image ships ICU, which the Persian calendar and culture handling need.
# An Alpine variant would require icu-libs to be installed explicitly.
ENV DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish .

# Two directories that must outlive the container:
#   keys  — data-protection key ring; losing it invalidates every authentication cookie
#   media — uploaded application icons; losing it leaves rows pointing at missing files
# Both belong on mounted volumes. They are deliberately outside the web root, so nothing here
# is ever served by the static-file middleware.
RUN mkdir -p /var/sentinel/keys /var/sentinel/media \
    && chown -R $APP_UID:$APP_UID /var/sentinel

ENV MediaStorage__RootPath=/var/sentinel/media

# The base image provides a non-root user; the application never needs root.
USER $APP_UID

EXPOSE 8080

# No HEALTHCHECK instruction: the runtime image deliberately has no curl or wget, and adding
# one would widen the attack surface for no gain. Point your orchestrator's HTTP probes at
# /health/live (liveness) and /health/ready (readiness) instead.

ENTRYPOINT ["dotnet", "Sentinel.Web.dll"]
