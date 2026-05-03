---
name: compile
description: How to compile the project or third-party libraries
---

# Compile SomeEngine

Follow these project-local build rules before running dotnet commands:

- `dotnet build/run` commands that may restore packages must be run outside the sandbox.
- When Python is needed, prefer `uv`/venv and run it outside the sandbox.
- Do not edit `external/DiligentCore` source or project files just to change how SomeEngine references Diligent. Keep local reference policy in SomeEngine MSBuild props/targets.

## Default package build

Use this for the normal SomeEngine build. It references the configured NuGet/local-feed package, currently `DiligentGraphics.DiligentEngine.Core`.

```powershell
dotnet build SomeEngine.slnx -v minimal
```

Do not pass `-p:Platform=x64` at the `.slnx` level. The solution configuration is `Any CPU`; project-level MSBuild props normalize empty/`AnyCPU`/`Any CPU` to `x64` for Diligent consumers.

For an individual project build, passing `-p:Platform=x64` is fine:

```powershell
dotnet build src\SomeEngine.Runtime\SomeEngine.Runtime.csproj -p:Platform=x64 -v minimal
```

## Local Diligent build mode

Use this when actively debugging or changing Diligent locally and you do not want to bump the NuGet package version.

First build/update the local Diligent C# artifacts from the repo root:

```powershell
uv run .\external\DiligentCore\BuildTools\.NET\dotnet-build-package.py -c Debug -d .\external\DiligentCore
```

Then build SomeEngine against the local Diligent build output:

```powershell
dotnet build SomeEngine.slnx -p:DiligentUseLocalBuild=true -p:DiligentConfiguration=Debug -v minimal
```

In local mode, SomeEngine references:

```text
external\DiligentCore\build\.NET\Graphics\GraphicsEngine.NET\bin\x64\Debug\net6.0\Diligent-GraphicsEngine.NET.dll
```

Native Diligent DLLs are copied from the generated local Diligent native output by `build\Diligent.LocalBuild.targets`.

## Release local build

For a Release local Diligent build:

```powershell
uv run .\external\DiligentCore\BuildTools\.NET\dotnet-build-package.py -c Release -d .\external\DiligentCore
dotnet build SomeEngine.slnx -c Release -p:DiligentUseLocalBuild=true -p:DiligentConfiguration=Release -v minimal
```

## Notes

- `DiligentUseLocalBuild=false` is the default.
- `DiligentUseLocalBuild=true` removes the Diligent Core package reference from SomeEngine consumers and uses the local managed/native outputs instead.
- If stale NuGet restore assets keep importing old Diligent package targets, run the SomeEngine `dotnet build` command without `--no-restore` so assets are regenerated.
- The generated Diligent C# binding source is under:

```text
external\DiligentCore\build\.NET\Graphics\GraphicsEngine.NET
```
