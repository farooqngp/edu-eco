# syntax=docker/dockerfile:1
# Build context: ./EduEco (solution folder). See docker-compose.yml at repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore layer: only project files + central build/package props.
COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY Core/Core.csproj Core/
COPY Application/Application.csproj Application/
COPY Infrastructure/Infrastructure.csproj Infrastructure/
COPY ServiceRegistry/ServiceRegistry.csproj ServiceRegistry/
COPY EduEco.Database/EduEco.Database.csproj EduEco.Database/
RUN dotnet restore EduEco.Database/EduEco.Database.csproj

COPY Core/ Core/
COPY Application/ Application/
COPY Infrastructure/ Infrastructure/
COPY ServiceRegistry/ ServiceRegistry/
COPY EduEco.Database/ EduEco.Database/
RUN dotnet publish EduEco.Database/EduEco.Database.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "EduEco.Database.dll"]
CMD ["migrate"]
