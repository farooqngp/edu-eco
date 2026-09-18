# syntax=docker/dockerfile:1
# Build context: ./EduEco (solution folder). See docker-compose.yml at repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY Core/Core.csproj Core/
COPY Application/Application.csproj Application/
COPY Infrastructure/Infrastructure.csproj Infrastructure/
COPY ServiceRegistry/ServiceRegistry.csproj ServiceRegistry/
COPY EduEco.Api/EduEco.Api.csproj EduEco.Api/
RUN dotnet restore EduEco.Api/EduEco.Api.csproj

COPY Core/ Core/
COPY Application/ Application/
COPY Infrastructure/ Infrastructure/
COPY ServiceRegistry/ ServiceRegistry/
COPY EduEco.Api/ EduEco.Api/
RUN dotnet publish EduEco.Api/EduEco.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 7037
ENTRYPOINT ["dotnet", "EduEco.Api.dll"]
