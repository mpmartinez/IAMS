# IAMS - IT Asset Management System
# Multi-stage Dockerfile for API and Blazor WebAssembly

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files only, so the restore layer caches until a dependency actually changes.
COPY src/IAMS.Api/IAMS.Api.csproj src/IAMS.Api/
COPY src/IAMS.Web/IAMS.Web.csproj src/IAMS.Web/
COPY src/IAMS.Shared/IAMS.Shared.csproj src/IAMS.Shared/

# Restore the two publishable projects explicitly, NOT the solution.
#
# IAMS.sln is deliberately not copied. A bare `dotnet restore` restores every project the
# solution lists, which now includes tests/IAMS.Api.Tests - a project this image neither
# copies nor needs. That combination fails the build with a bare "dotnet restore did not
# complete successfully", which is a long way from the actual cause.
#
# Restoring per project keeps the image independent of solution membership, so adding
# another test or tooling project can never break the deployment build again.
RUN dotnet restore src/IAMS.Api/IAMS.Api.csproj
RUN dotnet restore src/IAMS.Web/IAMS.Web.csproj

# Copy source code
COPY src/ src/

# Build and publish API
RUN dotnet publish src/IAMS.Api/IAMS.Api.csproj -c Release -o /app/api --no-restore

# Build and publish Web (Blazor WASM)
RUN dotnet publish src/IAMS.Web/IAMS.Web.csproj -c Release -o /app/web --no-restore

# Runtime stage for API
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app

# Create directory for SQLite database and uploads
RUN mkdir -p /app/data /app/Uploads

# Copy published API
COPY --from=build /app/api .

# Copy Blazor WASM files to wwwroot for serving as static files
COPY --from=build /app/web/wwwroot ./wwwroot/app

# Environment variables
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
# No ConnectionStrings__DefaultConnection default: the app is PostgreSQL-only now, and a
# baked-in SQLite path would just make the container fail in a confusing way. Supply the
# Neon connection string from the orchestrator's environment.

# Expose port
EXPOSE 5000

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "IAMS.Api.dll"]

# Nginx stage for Web (optional - use this if you want separate web container)
FROM nginx:alpine AS web
WORKDIR /usr/share/nginx/html

# Copy Blazor WASM published files
COPY --from=build /app/web/wwwroot .

# Copy nginx configuration
COPY nginx.conf /etc/nginx/nginx.conf

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
