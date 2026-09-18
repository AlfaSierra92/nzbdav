# PAR2 tests

Run with the .NET 10 SDK:

```sh
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj
```

The suite covers recovery against real PAR2 fixtures, streaming reconstruction,
packet parsing, volume scanning, file matching, slice mapping, repair settings,
storage budgets, concurrency limits, recovery blobs and playback overlays.
Integration tests exercise the health-check repair branches, the next health check
after repair, missing recovery blobs and the authenticated POST repair endpoint.

Ported from upstream commits `a6c6f2f2e55dd900950c0c29b29b0f1074a01efe`,
`278cecec51f84cbee9102427c9a487aa3a127602`,
`aa00df1b4586a9bbff04c1c8e2170dd4d99787be` and
`2f2bede956913676e5fcd3f00deb0335206ca752`, including their recovery-storage,
overlay and pipeline dependencies.

The settings validation tests run independently with Node 24, without installing
frontend dependencies:

```sh
node --test tests/test_par2_settings.mjs
```

See [PAR2 integration notes](../../docs/par2-repair-design.md) for supported
file types, configuration and the manual API.
