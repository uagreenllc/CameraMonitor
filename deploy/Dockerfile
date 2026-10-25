FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY CameraMonitor.sln ./
COPY src/CameraMonitor.Api/CameraMonitor.Api.csproj src/CameraMonitor.Api/
COPY tests/CameraMonitor.Tests/CameraMonitor.Tests.csproj tests/CameraMonitor.Tests/
RUN dotnet restore CameraMonitor.sln
COPY . .
RUN dotnet publish src/CameraMonitor.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg tzdata curl \
 && rm -rf /var/lib/apt/lists/*
ENV TZ=Europe/Kyiv \
    ASPNETCORE_HTTP_PORTS=8080 \
    Server__DataDir=/data \
    Server__LiveDir=/run/live \
    Server__CamerasFile=/app/cameras.json
WORKDIR /app
COPY --from=build /app .
USER app
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "CameraMonitor.Api.dll"]
