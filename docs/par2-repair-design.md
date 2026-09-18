# PAR2 repair integration

This branch ports the following upstream changes into the existing backend and
React Bootstrap settings page:

| Commit | Functionality |
| --- | --- |
| `a6c6f2f` | GF(2^16), Reed-Solomon reconstruction, PAR2 packet parsing, recovery-volume scanning, file matching and slice mapping |
| `278cecec` | Four `repair.par2.*` settings and fallback policy |
| `aa00df1b` | Attempt PAR2 repair during health checks before Arr replacement; exclude recovered articles from subsequent checks |
| `2f2bede9` | PAR2 controls in Settings → Repairs and an authenticated manual repair endpoint |

The latter two changes require the repair pipeline (upstream `10ed6fbd`), recovery
blob persistence and WebDAV overlays. These dependencies are included. The SQLite
migration adds `DavItems.RecoveryBlobId` and a deletion trigger which schedules
recovery blobs for the existing cleanup service. The migration has been added to
the source; this change does not migrate or deploy a running installation.

## Behavior

Background repairs retain their existing prerequisites: a configured library
directory, Radarr/Sonarr and enabled Background Repairs. With PAR2 enabled, a
linked unhealthy file gets a recovery attempt after the blocklist/orphan rules
and before the existing replacement flow.

The service uses the item's saved NZB to locate PAR2 volumes, including detection
by magic bytes when names are obfuscated. It maps missing articles to slices,
downloads recovery data, reconstructs missing bytes and checks IFSC MD5 hashes
before saving. Only reconstructed ranges and their metadata are persisted.

WebDAV reads combine surviving Usenet data with locally recovered ranges. The
local stream implementation uses actual yEnc offsets, so known dead segment IDs
are removed from the underlying stream to avoid failed seek probes and prefetches.
Health checks decode the recovery blob's own metadata format and omit reconstructed
articles only while the blob exists.

Successful automatic and manual repairs each record one history entry using the
existing Repaired action. Automatic failures use the configured fallback.

## Settings

| Key | Default | Meaning |
| --- | --- | --- |
| `repair.par2.enable` | `false` | Enable automatic PAR2 attempts |
| `repair.par2.fallback` | `arr-research` | Existing replacement flow; alternatively `mark-only` or `delete` |
| `repair.par2.max-storage-bytes` | `0` | Unlimited, or a byte budget for saved repairs |
| `repair.par2.max-concurrent` | `1` | Maximum concurrent repair attempts |

A repair exceeding the entire storage budget is refused. To admit a smaller new
repair, the budget policy can evict the oldest repaired items and request Arr
replacements. Cleanup of their blobs is asynchronous. Attempts above the concurrency
limit return NotAttempted; automatic checks then use the configured fallback.

## Manual API

Send an authenticated request to the backend:

```http
POST /api/repair-item?davItemId=<item-guid>
x-api-key: <FRONTEND_BACKEND_API_KEY>
```

The response includes `status`, `outcome` (`NotAttempted`, `Repaired`, `Infeasible`
or `Failed`) and `message`. Missing items return 404, malformed identifiers 400,
and GET requests 405. Manual repair is an explicit attempt independent of the
automatic enable toggle; it respects the concurrency and storage settings. It
does not apply automatic fallback deletion/replacement when repair fails.

## Current limits and verification

Repair supports plain NZB files and legacy RAR items. MultipartFile repair is
not supported by this upstream pipeline. A saved NZB and enough readable recovery
data are required; every protected file must match this item's posted files.

Added tests cover the engine, storage, playback, health-check integration and API.
The local Node settings tests, SQLite trigger checks and PAR2 fixture integrity
checks passed. The .NET suite and full frontend typecheck have not run in this
workspace because the .NET SDK and frontend dependencies are unavailable.

```sh
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj
node --test tests/test_par2_settings.mjs
cd frontend
npm run typecheck
```

The earlier requested NNTP commit `97db88a2` was not applied: its pipelined STAT
path is absent from this branch, which uses the concurrent check directly.
