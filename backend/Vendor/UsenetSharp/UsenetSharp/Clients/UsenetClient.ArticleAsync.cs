using System.IO.Pipelines;
using UsenetSharp.Diagnostics;
using UsenetSharp.Streams;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public Task<UsenetArticleResponse> ArticleAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return ArticleAsync(segmentId, null, cancellationToken);
    }

    public async Task<UsenetArticleResponse> ArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _commandLock.WaitAsync(cancellationToken);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        var isReadBodyToPipeAsyncStarted = false;

        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            // Send ARTICLE command with message-id
            await WriteLineAsync($"ARTICLE <{segmentId}>".AsMemory(), _cts.Token);
            var response = await ReadLineAsync(_cts.Token);
            var responseCode = ParseResponseCode(response);

            // Article retrieved - head and body follow
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            {
                // Parse headers
                var headers = await ParseArticleHeadersAsync(_cts.Token);

                // Create a pipe for streaming the body data
                var pipe = new Pipe(new PipeOptions(
                    pool: ArticleMemory.TransportPool,
                    pauseWriterThreshold: long.MaxValue,
                    resumeWriterThreshold: long.MaxValue - 1
                ));

                var buffered = ArticleMemory.TrackBufferedData();

                // Start background task to read the body and write to pipe
                isReadBodyToPipeAsyncStarted = true;
                _ = ReadBodyToPipeAsync(pipe.Writer, buffered, _cts.Token, () =>
                {
                    _commandLock.Release();
                    onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
                });

                // Return immediately with the stream and headers
                return new UsenetArticleResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    ArticleHeaders = headers,
                    Stream = new TrackedArticleStream(pipe.Reader.AsStream(), buffered),
                };
            }

            return new UsenetArticleResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                SegmentId = segmentId,
                Stream = null,
                ArticleHeaders = null
            };
        }
        finally
        {
            if (!isReadBodyToPipeAsyncStarted)
            {
                _commandLock.Release();
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            }
        }
    }

    private async Task<UsenetArticleHeader> ParseArticleHeadersAsync(CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentHeaderName = null;
        var currentHeaderValue = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadLineAsync(cancellationToken);

            if (line == null)
            {
                throw new UsenetProtocolException("Invalid NNTP response: missing article headers.");
            }

            // Empty line signals end of headers
            if (string.IsNullOrEmpty(line) || line == ".")
            {
                // Save the last header if any
                if (currentHeaderName != null)
                {
                    headers[currentHeaderName] = currentHeaderValue.ToString().Trim();
                }

                break;
            }

            // Check if this is a continuation line (starts with whitespace)
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                // Append to current header value
                if (currentHeaderName != null)
                {
                    currentHeaderValue.Append(' ');
                    currentHeaderValue.Append(line.Trim());
                }
            }
            else
            {
                // Save the previous header if any
                if (currentHeaderName != null)
                {
                    headers[currentHeaderName] = currentHeaderValue.ToString().Trim();
                }

                // Parse new header: "Name: Value"
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    currentHeaderName = line.Substring(0, colonIndex).Trim();
                    currentHeaderValue.Clear();

                    // Get value after colon
                    if (colonIndex + 1 < line.Length)
                    {
                        currentHeaderValue.Append(line.Substring(colonIndex + 1).Trim());
                    }
                }
            }
        }

        return new UsenetArticleHeader
        {
            Headers = headers
        };
    }
}