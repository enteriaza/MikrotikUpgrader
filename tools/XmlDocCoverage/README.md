# XML documentation coverage audit

Offline Roslyn syntax audit for hand-maintained C# under `src/MikrotikWorker` and `tests/MikrotikWorker.Tests`.

It enumerates eligible type/member declarations (including `private` / `internal` and test helpers) and requires a meaningful `///` comment containing `<summary>` or `<inheritdoc`.

## Run

```powershell
dotnet run --project tools/XmlDocCoverage/XmlDocCoverage/XmlDocCoverage.csproj -c Release -- <repo-root>
```

Exit code `0` means full coverage; `1` means missing documentation. Writes `tools/XmlDocCoverage/coverage-report.txt`.

Not part of the production runtime or the main solution build.
