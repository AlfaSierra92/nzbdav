using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Fakes;

/// <summary>
/// An in-memory usenet server backed by a dictionary of decoded article bodies.
/// Articles can be marked dead to simulate DMCA/retention losses; dead articles throw
/// UsenetArticleNotFoundException exactly like the real client.
/// </summary>
/// <remarks>
/// Unlike a fake that replays a fixed
/// segment dictionary; this one builds a synthetic corpus (<see cref="AddFile"/>) whose
/// articles can be killed afterwards, which is what PAR2 repair has to be exercised
/// against. Extends NntpClient so it inherits the stream defaults rather
/// than re-implementing the whole INntpClient surface.
/// </remarks>
public sealed class SyntheticUsenetClient : NntpClient
{
    private sealed record Article(long PartOffset, byte[] Body);

    private readonly Dictionary<string, Article> _articles = new();
    private readonly HashSet<string> _dead = [];

    public void AddArticle(string segmentId, long partOffset, byte[] body)
        => _articles[segmentId] = new Article(partOffset, body);

    /// <summary>Splits a file into fixed-size segments with ids "{prefix}-{i}".</summary>
    public string[] AddFile(string prefix, byte[] data, int segmentSize)
    {
        var ids = new List<string>();
        for (var offset = 0; offset < data.Length; offset += segmentSize)
        {
            var id = $"{prefix}-{ids.Count}";
            AddArticle(id, offset, data[offset..Math.Min(offset + segmentSize, data.Length)]);
            ids.Add(id);
        }

        return ids.ToArray();
    }

    public void MarkDead(params string[] segmentIds)
    {
        foreach (var id in segmentIds) _dead.Add(id);
    }

    private Article GetAliveArticle(string segmentId)
    {
        if (_dead.Contains(segmentId) || !_articles.TryGetValue(segmentId, out var article))
            throw new UsenetArticleNotFoundException(segmentId);
        return article;
    }

    private UsenetYencHeader HeaderOf(string segmentId)
    {
        var article = GetAliveArticle(segmentId);
        return new UsenetYencHeader
        {
            FileName = "fake.bin",
            FileSize = 0,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartOffset = article.PartOffset,
            PartSize = article.Body.Length,
        };
    }

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
        => Task.FromResult(new UsenetResponse { ResponseCode = 281, ResponseMessage = "ok" });

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        var id = segmentId.ToString();
        var exists = !_dead.Contains(id) && _articles.ContainsKey(id);
        return Task.FromResult(new UsenetStatResponse
        {
            ResponseCode = exists ? 223 : 430,
            ResponseMessage = exists ? "exists" : "no such article",
            ArticleExists = exists,
        });
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
        => DecodedBodyAsync(segmentId, null, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = segmentId.ToString();
        try
        {
            var article = GetAliveArticle(id);
            var response = new UsenetDecodedBodyResponse
            {
                ResponseCode = 222,
                ResponseMessage = "body follows",
                SegmentId = id,
                Stream = new CachedYencStream(HeaderOf(id), new MemoryStream(article.Body)),
            };
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(response);
        }
        catch (Exception e)
        {
            // Faulted task rather than a throw, so pipelined batch consumers can await
            // per-segment failures without aborting DecodedBodiesAsync itself. A dead
            // article has to fail exactly where a real one would.
            return Task.FromException<UsenetDecodedBodyResponse>(e);
        }
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    // The base derives these from HeadAsync, which this fake does not serve.
    public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
        => Task.FromResult(HeaderOf(segmentId));

    public override Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct)
    {
        var last = GetAliveArticle(file.Segments[^1].MessageId);
        return Task.FromResult(last.PartOffset + last.Body.Length);
    }

    public override void Dispose()
    {
    }
}
