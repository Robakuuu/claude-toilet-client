FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ClaudeToiletClient.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish

# Compile the native PTY helper
FROM gcc:12 AS native-build
COPY Native/pty-helper.c /tmp/pty-helper.c
RUN gcc -shared -fPIC -o /tmp/libptyhelper.so /tmp/pty-helper.c -lutil

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install util-linux (nsenter) to break into host namespaces
RUN apt-get update && \
    apt-get install -y --no-install-recommends util-linux && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY --from=native-build /tmp/libptyhelper.so /app/libptyhelper.so

ENV ASPNETCORE_URLS=http://0.0.0.0:5000
ENV ASPNETCORE_ENVIRONMENT=Production
ENV LD_LIBRARY_PATH=/app

EXPOSE 5000

ENTRYPOINT ["dotnet", "ClaudeToiletClient.dll"]
