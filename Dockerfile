#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

# FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-buster-slim AS base
# WORKDIR /app

# FROM mcr.microsoft.com/dotnet/core/sdk:3.1-buster AS build
# WORKDIR /src
# COPY ["ValhallaHeimdall.csproj", ""]
# RUN dotnet restore "./ValhallaHeimdall.csproj"
# COPY . .
# WORKDIR "/src/."
# RUN dotnet build "ValhallaHeimdall.csproj" -c Release -o /app/build

# FROM build AS publish
# RUN dotnet publish "ValhallaHeimdall.csproj" -c Release -o /app/publish

# FROM base AS final
# WORKDIR /app
# COPY --from=publish /app/publish .
# CMD ASPNETCORE_URLS = http://*:$PORT dotnet ValhallaHeimdall.API.dll

# COPY . /app
# WORKDIR /app/ValhallaHeimdall.API
# RUN ["dotnet", "restore"]
# WORKDIR /app/ValhallaHeimdall.BLL
# RUN ["dotnet", "restore"]
# WORKDIR /app/ValhallaHeimdall.DAL
# RUN ["dotnet", "restore"]
# WORKDIR /app/ValhallaHeimdall.API
# RUN ["dotnet", "build"]
# CMD ASPNETCORE_URLS=http://*:$PORT dotnet ValhallaHeimdall.API.dll


FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-buster-slim AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/core/sdk:3.1-buster AS build
WORKDIR /src

COPY ValhallaHeimdall.sln ./
COPY ValhallaHeimdall.API/* csproj ./ValhallaHeimdall/
# RUN dotnet restore "./ValhallaHeimdall.API.csproj"
COPY ValhallaHeimdall.DAL/*.csproj ./ValhallaHeimdall/
# RUN dotnet restore "./ValhallaHeimdall.DAL.csproj"
COPY ValhallaHeimdall.BLL/*.csproj ./ValhallaHeimdall/
# RUN dotnet restore "./ValhallaHeimdall.BLL.csproj"

RUN dotnet restore
COPY . .

WORKDIR /src/ValhallaHeimdall.API
RUN dotnet build -c Release -o /app/build

WORKDIR /src/ValhallaHeimdall.DAL
RUN dotnet build -c Release -o /app/build

WORKDIR /src/ValhallaHeimdall.DAL
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ValhallaHeimdall.API.dll"]