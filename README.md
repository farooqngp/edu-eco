# SDLC

.NET monorepo skeleton: a single solution (`SDLC.sln`) hosting all services
and shared libraries for this project, with centralized build configuration
and NuGet package version management.

See [docs/architecture.md](docs/architecture.md) for the full repository
layout and conventions.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build & test

```bash
dotnet restore
dotnet build
dotnet test
```

Or use the wrapper scripts:

```bash
./build/build.sh        # macOS/Linux
./build/build.ps1       # Windows
```

## Repository structure

```
src/            buildable projects
tests/          test projects (mirrors src/)
build/          local build scripts
docs/           architecture and process docs
.github/        CI workflows
```
