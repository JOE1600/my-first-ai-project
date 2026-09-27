# Production image for the Boxwood enquiry API. Works on any host that runs containers
# (Fly.io, Render, Railway, Azure Container Apps, a VPS with Docker, ...).
#
# The code is built for .NET 10 LTS here, even though the development Mac builds it for .NET 6,
# because .NET 6 no longer receives security fixes.
#
#   docker build -t boxwood-api backend
#   docker run -p 8080:8080 -v boxwood-data:/data --env-file boxwood.env boxwood-api

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend.csproj .
RUN dotnet restore -p:BoxwoodTargetFramework=net10.0
COPY . .
RUN dotnet publish -c Release -p:BoxwoodTargetFramework=net10.0 --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    BOXWOOD_DATA_DIR=/data
# The database lives on /data. Mount a persistent volume there, or every redeploy starts empty.
RUN mkdir -p /data && chown "$APP_UID" /data && chmod 700 /data
COPY --from=build /app .
USER $APP_UID
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "backend.dll"]
