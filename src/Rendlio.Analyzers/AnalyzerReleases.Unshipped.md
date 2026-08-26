; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|---------------------|----------|--------------------------------------------------
RENDLIO001 | Rendlio.Security | Error | Banned API — process spawning, dynamic code loading, reflection over type names, network I/O, and native interop declarations.
RENDLIO002 | Rendlio.Determinism | Error | Non-deterministic API — DateTime.Now, System.Random and Guid.NewGuid.
