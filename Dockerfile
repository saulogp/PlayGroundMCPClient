FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/PlayGroundMCPClient.Web/*.csproj ./PlayGroundMCPClient.Web/
RUN dotnet restore ./PlayGroundMCPClient.Web/PlayGroundMCPClient.Web.csproj
COPY src/PlayGroundMCPClient.Web/. ./PlayGroundMCPClient.Web/
RUN dotnet publish ./PlayGroundMCPClient.Web/PlayGroundMCPClient.Web.csproj \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
# Mount these as volumes so they persist across container restarts:
#   /app/playground.db
#   /app/mcp-servers.user.json
ENTRYPOINT ["dotnet", "PlayGroundMCPClient.Web.dll"]
