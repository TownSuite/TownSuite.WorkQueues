# Build from the repository root:
#   docker buildx build -f TownSuite.WorkQueues.Server/Dockerfile .
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["TownSuite.WorkQueues/TownSuite.WorkQueues.csproj", "TownSuite.WorkQueues/"]
COPY ["TownSuite.WorkQueues.Client/TownSuite.WorkQueues.Client.csproj", "TownSuite.WorkQueues.Client/"]
COPY ["TownSuite.WorkQueues.Server/TownSuite.WorkQueues.Server.csproj", "TownSuite.WorkQueues.Server/"]
RUN dotnet restore "TownSuite.WorkQueues.Server/TownSuite.WorkQueues.Server.csproj"
COPY TownSuite.WorkQueues/ TownSuite.WorkQueues/
COPY TownSuite.WorkQueues.Client/ TownSuite.WorkQueues.Client/
COPY TownSuite.WorkQueues.Server/ TownSuite.WorkQueues.Server/
RUN dotnet publish "TownSuite.WorkQueues.Server/TownSuite.WorkQueues.Server.csproj" -c Release --no-restore -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "TownSuite.WorkQueues.Server.dll"]
