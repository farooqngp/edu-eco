# syntax=docker/dockerfile:1
# Build context: ./EduEco (solution folder). See docker-compose.yml at repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY Core/Core.csproj Core/
COPY Application/Application.csproj Application/
COPY Infrastructure/Infrastructure.csproj Infrastructure/
COPY ServiceRegistry/ServiceRegistry.csproj ServiceRegistry/
COPY EduEco.Identity/EduEco.Identity.csproj EduEco.Identity/
RUN dotnet restore EduEco.Identity/EduEco.Identity.csproj

COPY Core/ Core/
COPY Application/ Application/
COPY Infrastructure/ Infrastructure/
COPY ServiceRegistry/ ServiceRegistry/
COPY EduEco.Identity/ EduEco.Identity/
RUN dotnet publish EduEco.Identity/EduEco.Identity.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Data Protection key ring volume must be writable by the non-root app user.
RUN mkdir -p /keys && chown "$APP_UID" /keys

COPY --from=build /app .
USER $APP_UID
EXPOSE 8443
ENTRYPOINT ["dotnet", "EduEco.Identity.dll"]
