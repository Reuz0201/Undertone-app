using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Undertone.Core;

namespace Undertone.Tests;

internal static class TestData
{
    public static YaTrack Track(string id, int seconds = 180) => new()
    {
        Id = id,
        AlbumId = "a" + id,
        Title = "Трек " + id,
        Artists = new[] { "Артист" },
        DurationMs = seconds * 1000L,
    };

    public static YaTrack[] Tracks(int count) => Enumerable.Range(1, count).Select(i => Track(i.ToString())).ToArray();
}

/// <summary>Подменяет сеть: на каждый запрос отвечает функция.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string> _respond;
    public int Calls;

    public FakeHandler(Func<HttpRequestMessage, string> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        var body = _respond(request);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
