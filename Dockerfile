# syntax=docker/dockerfile:1
FROM node:22-alpine AS frontend
WORKDIR /source/web
COPY web/package*.json ./
RUN npm install --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/EgymGarminSync.Web/EgymGarminSync.Web.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
COPY --from=frontend /source/web/dist ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production \
    EGYM_SYNC_DATA_DIR=/data \
    ASPNETCORE_URLS=http://0.0.0.0:5187
RUN mkdir -p /data
EXPOSE 5187
VOLUME ["/data"]
ENTRYPOINT ["dotnet","EgymGarminSync.Web.dll"]
