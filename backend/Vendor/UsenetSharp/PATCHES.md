# Local UsenetSharp instrumentation

Based on the UsenetSharp 1.0.6 source snapshot already present in this workspace. An upstream commit hash has not been verified. The original README and MIT LICENSE are retained alongside this file.

Local changes:

- Build for net10.0 alongside the backend, using Microsoft.AspNetCore.App for Pipelines; retain RapidYencSharp 1.0.0.
- Track actual MemoryPool capacity owned by BODY/ARTICLE pipes and ArrayPool capacity owned by yEnc decoders.
- Expose allocated/unread bytes, transport/decoder breakdown, lifetime peak, optional global capacity limit and rejected allocations via ArticleMemory.
- Track pipe consumption and decoder leftovers; release reservations on disposal and partial constructor failure.
- Allow derived decorators/cache streams to skip unused decoder buffers.
- Complete article pipes with producer exceptions so failures, including capacity rejections, reach readers.

The capacity limit fails allocations immediately; it does not implement backpressure or disk spill and does not cap process RSS or idle pool retention. See docs/dashboard.md for configuration and persistence semantics.

The backend uses a ProjectReference instead of the NuGet UsenetSharp package. When updating this snapshot, preserve/reapply these patches and run tests/ArticleMemory plus tests/DashboardBuffer and backend/frontend builds.
