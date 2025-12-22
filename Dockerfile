# IAMS - IT Asset Management System
# Multi-stage Dockerfile for API and Blazor WebAssembly

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY IAMS.sln ./
COPY src/IAMS.Api/IAMS.Api.csproj src/IAMS.Api/
COPY src/IAMS.Web/IAMS.Web.csproj src/IAMS.Web/
COPY src/IAMS.Shared/IAMS.Shared.csproj src/IAMS.Shared/

# Restore dependencies
RUN dotnet restore

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
ENV ConnectionStrings__DefaultConnection="Data Source=/app/data/iams.db"

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
