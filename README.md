# MethodMapper

MethodMapper starts at a C# method you choose and writes an indented text report of every method it calls, then every method those call, and so on to the bottom. It follows calls across classes, projects, and DLLs whose source projects are in the same solution.

```
[#1] Method: void Root.Run() | Class: App.Root | Project: App | Assembly: App [root]
  [#2] Method: void Root.CrossProject() | ... | Project: App [call at App.cs:10]
    [#6] Method: Customer Service.Find(int id) | ... | Project: Core [call at App.cs:14]
      [#7] Method: T IRepo<T>.Get(int id) | ... | Project: Lib [call at Core.cs:7]
      [#8] Method: Customer CustomerRepo.Get(int id) | ... | Project: Lib [possible dispatch from ...]
```

Each entry shows the method, class, project, assembly, and the call site that reached it. Every method is expanded once; `[see #N]` points to its first expansion, so recursion and shared branches do not multiply the report.

## Requirements

- The .NET SDK (the build uses .NET 10; the tool rolls forward to the newest runtime installed, so a newer SDK works).
- The solution you analyze must be restored and buildable: its own SDKs, targeting packs, and NuGet packages must be installed. It does not need to target the same framework as MethodMapper.

## Build

```powershell
dotnet build ConsoleApp1\MethodMapper.csproj
```

## Run

Supply a `.sln` or `.slnx` file (not a `.csproj`):

```powershell
dotnet run --project ConsoleApp1\MethodMapper.csproj -- `
  --solution "D:\MyApp\MyApp.sln" --project MyProject `
  --class MyNamespace.MyClass --method DoWork --out "D:\DoWork.txt"
```

The first `--project` after `dotnet run` would belong to `dotnet`; the one after the standalone `--` is MethodMapper's own.

With no arguments, MethodMapper prompts for the solution, project, class, and method.

| Argument | Meaning |
| --- | --- |
| `--solution` | Required. Solution file to load. |
| `--project` | Required. Project containing the method, matched by name (case-insensitive). A project that targets several frameworks may be given by its plain name. |
| `--class` | Required. Class including namespace. Nested classes use `Namespace.Outer+Inner`; generics use a backtick and parameter count, such as ``Namespace.Box`1``. |
| `--method` | Required. Method declared on that class. Use `.ctor` for a constructor. |
| `--signature` | Optional, case-sensitive fragment of the displayed signature, to pick one overload. |
| `--out` | Report path ending in `.txt`. Default `call-hierarchy.txt`. Replaced only when the run succeeds. |
| `--configuration` | Build configuration. Default `Debug`. |
| `--framework` | Target framework such as `net10.0`. Default: each project's own. |
| `--max-depth` | Do not expand methods deeper than this many levels below the root. They are listed as `[depth limit]`. |
| `--exclude-external` | Omit methods whose source is not in the solution (framework, NuGet packages, DLLs without a source project). The report notes how many were omitted. |
| `--verbose` | Show full exception details on failure. |
| `--help` | Show usage. |

Press Ctrl+C to cancel; no partial report is left behind. If a method name or overload is wrong, the error lists the methods that are available.

A `samples\Sample` solution (App → Core → Lib, plus a multi-targeted project) is included for trying the tool. Build it first, then:

```powershell
dotnet build samples\Sample\Sample.slnx
dotnet run --project ConsoleApp1\MethodMapper.csproj -- --solution samples\Sample\Sample.slnx --project App --class App.Root --method Run
```

## What is followed

- Method and constructor calls, property and event accessors, user-defined operators and conversions, local functions, lambda bodies, and method groups passed as callbacks.
- Interface and virtual calls, with every implementation or override found in the solution listed as `possible dispatch`. These are alternatives, not proof of what runs.
- Constructors include their instance field and property initializers, and primary-constructor classes are handled the same way.
- `foreach`, `using`, and `await` include the enumerator, `Dispose`, and awaiter calls of your own types.
- Calls into another project, or into a DLL whose project is in the solution, continue into that project's source.

## What is not followed

- Bodies of DLLs without a source project in the solution. They appear as terminal entries.
- Reflection, `dynamic`, and delegates bound at runtime (reported as `UNRESOLVED`).
- Static constructors and static field initializers, and implicit `ToString` or string-conversion calls.
- Code excluded by conditional compilation, and only the loaded configuration and framework.
- Compilation errors can hide calls. Each file with errors is flagged in the report the first time it is analyzed.

Call-site order is source order, not execution order. `Unique methods` counts external methods listed as terminal entries.

## Troubleshooting

- **`No instances of MSBuild could be detected`:** the tool must run on a runtime at least as new as the newest installed SDK. The project file sets `RollForward` to `LatestMajor` for this; rebuild if you see it on an old build.
- **Restore fails or NU1900 appears:** check that the NuGet feed is reachable. Offline machines need a populated package cache.
- **An SDK or targeting pack is missing:** install what the analyzed solution needs, then restore and build that solution on its own first.
- **A legacy project will not load:** projects needing Visual Studio's full .NET Framework MSBuild may need a different host.
- **Access denied:** check access to the solution, the output folder, and the SDK directories.

Workspace load problems are printed to the console and written at the top of the report.

## How it works

`Program` loads the solution with Roslyn's `MSBuildWorkspace`, `RootMethodFinder` locates the starting method, and `CallAnalyzer` walks the hierarchy depth-first with an explicit stack. `CallCollector` reads one method body's Roslyn operations for calls, and `HierarchyReport` writes the text. `AnalyzerOptions` parses arguments. The files deliberately have no namespace.

Dependencies: `Microsoft.Build.Locator` (finds the installed MSBuild), `Microsoft.CodeAnalysis.Workspaces.MSBuild` and `Microsoft.CodeAnalysis.CSharp.Workspaces` (load projects and resolve symbols). Keep the two `Microsoft.CodeAnalysis` packages at the same version.
