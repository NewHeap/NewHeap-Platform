using System.Net;

namespace NewHeap.Platform.Common.Services.Api;

/// <summary>
/// Owns an unbuffered API response and all request resources that must stay alive
/// while its content is being consumed.
/// </summary>
public sealed class NhApiHttpResponse : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpRequestMessage _request;
    private bool _disposed;

    internal NhApiHttpResponse(
        HttpClient httpClient,
        HttpRequestMessage request,
        HttpResponseMessage response)
    {
        _httpClient = httpClient;
        _request = request;
        Response = response;
    }

    public HttpResponseMessage Response { get; }

    public HttpContent Content => Response.Content;

    public HttpStatusCode StatusCode => Response.StatusCode;

    public Task<Stream> ReadAsStreamAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Content.ReadAsStreamAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Response.Dispose();
        }
        finally
        {
            try
            {
                _request.Dispose();
            }
            finally
            {
                _httpClient.Dispose();
            }
        }
    }
}
