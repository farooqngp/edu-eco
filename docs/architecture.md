# Repository Layout

This is a monorepo: all .NET services and libraries for this solution live in
one repository under a single solution file, sharing common build
configuration and package versions.

```
SDLC/
├── src/                          # Buildable projects
│   └── Shared/
│       └── SDLC.Shared/          # Sample shared class library
├── tests/                        # Test projects, mirroring src/
│   └── SDLC.Shared.Tests/
├── build/                        # Local build scripts (build.ps1 / build.sh)
├── docs/                         # Architecture and process docs
├── .github/workflows/            # CI pipelines
├── SDLC.sln                      # Solution file referencing all projects
├── Directory.Build.props         # Shared MSBuild properties for every project
├── Directory.Build.targets       # Shared MSBuild targets for every project
├── Directory.Packages.props      # Central NuGet package version management
├── global.json                   # Pinned .NET SDK version
└── NuGet.config                  # Package source configuration
```

## Conventions

- **Folder-per-area**: group projects under `src/<Area>/<ProjectName>` (e.g.
  `src/Services/`, `src/Shared/`). Each new service or library gets its own
  folder and its own `.csproj`.
- **Tests mirror src**: `tests/<ProjectName>.Tests` maps 1:1 to the project it
  covers.
- **Central package management**: add new NuGet dependencies to
  `Directory.Packages.props` with a pinned version; reference them by name
  only (no version) in individual `.csproj` files.
- **Shared MSBuild settings**: common properties (target framework, nullable,
  warnings-as-errors, output paths) live in `Directory.Build.props` so new
  projects inherit them automatically — don't repeat them per-project.
- **Build output**: all `bin`/`obj` output is redirected to `artifacts/` at
  the repo root (gitignored) to keep `src`/`tests` folders clean.

## Adding a new project

```bash
dotnet new <template> -n <ProjectName> -o src/<Area>/<ProjectName>
dotnet sln SDLC.sln add src/<Area>/<ProjectName>/<ProjectName>.csproj
```

Add a matching test project under `tests/` and reference it back to the new
project.
