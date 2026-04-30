# Multi-stage Dockerfile for Heimdall TicketTracker (Blazor Web App on .NET 10).

# ------------------------------------------------------------------
# Stage 1: Build front-end assets (Bootstrap) with Node.
# ------------------------------------------------------------------
FROM node:24.15.0-alpine AS assets
WORKDIR /src/web
RUN corepack enable && corepack prepare yarn@4.14.1 --activate
COPY src/Heimdall.Web/package.json src/Heimdall.Web/yarn.lock src/Heimdall.Web/.yarnrc.yml ./
RUN yarn install --immutable
COPY src/Heimdall.Web/build-assets.mjs ./
RUN yarn build

# ------------------------------------------------------------------
# Stage 2: Restore + build + publish the .NET solution.
# ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files first to maximize restore caching.
COPY src/Heimdall.Core/Heimdall.Core.csproj src/Heimdall.Core/
COPY src/Heimdall.DAL/Heimdall.DAL.csproj   src/Heimdall.DAL/
COPY src/Heimdall.BLL/Heimdall.BLL.csproj   src/Heimdall.BLL/
COPY src/Heimdall.Web/Heimdall.Web.csproj   src/Heimdall.Web/
RUN dotnet restore src/Heimdall.Web/Heimdall.Web.csproj

# Copy the rest of the source.
COPY src/ src/

# Bring in the prebuilt front-end assets from the assets stage.
COPY --from=assets /src/web/wwwroot/css/      src/Heimdall.Web/wwwroot/css/
COPY --from=assets /src/web/wwwroot/js/       src/Heimdall.Web/wwwroot/js/
COPY --from=assets /src/web/wwwroot/webfonts/ src/Heimdall.Web/wwwroot/webfonts/

RUN dotnet publish src/Heimdall.Web/Heimdall.Web.csproj \
    -c Release \
    -o /app/publish \
    /p:SkipYarnBuild=true

# ------------------------------------------------------------------
# Stage 3: Runtime image.
# ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./

# Render / PaaS injects $PORT; default to 8080 for local use.
ENV ASPNETCORE_ENVIRONMENT=Production \
    PORT=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Heimdall.Web.dll"]
