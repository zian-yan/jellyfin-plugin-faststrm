using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FastSTRM.Probing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastSTRM.Test;

public class RemoteSizeHttpTests
{
    private const long EightMegabytes = 8L * 1024 * 1024;

    [Fact]
    public async Task Query_RefusesLoopbackTargetWhenPrivateTargetsAreDisabled()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler, allowPrivateTargets: false);

        var size = await service.QueryRemoteSizeAsync("http://127.0.0.1:8096/movie.mkv", CancellationToken.None);

        Assert.Null(size);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Query_AllowsLoopbackTargetByDefault()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler, allowPrivateTargets: true);

        var size = await service.QueryRemoteSizeAsync("http://192.168.1.5:8096/movie.mkv", CancellationToken.None);

        Assert.Equal(EightMegabytes, size);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Query_RefusesCloudMetadataEndpointEvenWhenPrivateTargetsAreAllowed()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler, allowPrivateTargets: true);

        var size = await service.QueryRemoteSizeAsync("http://169.254.169.254/latest/meta-data/", CancellationToken.None);

        Assert.Null(size);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Query_UsesHeadContentLength()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);

        var size = await service.QueryRemoteSizeAsync("http://93.184.216.34/movie.mkv", CancellationToken.None);

        Assert.Equal(EightMegabytes, size);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, handler.Requests[0].Method);
    }

    [Fact]
    public async Task Query_RefusesAnHtmlErrorPageCarriedByA200()
    {
        var handler = new StubHandler(_ => Html(HttpStatusCode.OK, 8192));
        using var service = CreateService(handler);

        var size = await service.QueryRemoteSizeAsync("http://93.184.216.34/movie.mkv", CancellationToken.None);

        Assert.Null(size);
    }

    [Fact]
    public async Task Query_RefusesAnHtmlErrorPageCarriedByA206()
    {
        var handler = new StubHandler(_ => Html(HttpStatusCode.PartialContent, 2, total: 8192));
        using var service = CreateService(handler);

        var size = await service.QueryRemoteSizeAsync("http://93.184.216.34/movie.mkv", CancellationToken.None);

        Assert.Null(size);
    }

    [Fact]
    public async Task Query_FallsBackToRangedGetWhenHeadIsRejected()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            : PartialContent(0, 0, EightMegabytes));
        using var service = CreateService(handler);

        var size = await service.QueryRemoteSizeAsync("http://93.184.216.34/movie.mkv", CancellationToken.None);

        Assert.Equal(EightMegabytes, size);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("bytes=0-1", handler.Requests[1].Headers.Range?.ToString());
    }

    [Fact]
    public async Task Query_RejectsHtmlErrorPage()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, 900));
        using var service = CreateService(handler);

        var size = await service.QueryRemoteSizeAsync("http://93.184.216.34/movie.mkv", CancellationToken.None);

        Assert.Null(size);
    }

    [Fact]
    public async Task Query_AcceptsSmallButPlausibleRemoteSize()
    {
        const long fiveHundredKilobytes = 500L * 1024L;

        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, fiveHundredKilobytes));
        using var service = CreateService(handler);

        var size = await service.QueryRemoteSizeAsync("http://93.184.216.34/clip.mkv", CancellationToken.None);

        Assert.Equal(fiveHundredKilobytes, size);
    }

    [Fact]
    public async Task Resolve_ReusesUrlCacheAcrossItems()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);

        var first = WriteStrm("http://93.184.216.34/movie.mkv\n");
        var second = WriteStrm("http://93.184.216.34/movie.mkv\n");
        try
        {
            var sizeA = await service.ResolveAsync(new Video { Path = first, Size = 0 }, RemoteSizeTrigger.ManualIncrementalProbe, CancellationToken.None);
            var sizeB = await service.ResolveAsync(new Video { Path = second, Size = 0 }, RemoteSizeTrigger.ManualFullProbe, CancellationToken.None);

            Assert.Equal(EightMegabytes, sizeA);
            Assert.Equal(EightMegabytes, sizeB);

            Assert.Single(handler.Requests);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public async Task Resolve_SkipsAlreadyResolvedSmallSize()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, 500L * 1024L));
        using var service = CreateService(handler);

        var path = WriteStrm("http://93.184.216.34/clip.mkv\n");
        try
        {
            var size = await service.ResolveAsync(new Video { Path = path, Size = 500L * 1024L }, RemoteSizeTrigger.ManualIncrementalProbe, CancellationToken.None);

            Assert.Equal(500L * 1024L, size);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Resolve_ReadsCommentPrefixedStrmFile()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);

        var path = WriteStrm("# generated by the sync script\n\nhttp://93.184.216.34/movie.mkv\n");
        try
        {
            var size = await service.ResolveAsync(new Video { Path = path, Size = 0 }, RemoteSizeTrigger.ManualIncrementalProbe, CancellationToken.None);

            Assert.Equal(EightMegabytes, size);
            Assert.Single(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Resolve_IgnoresNonHttpUrlsInStrmFile()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);

        var path = WriteStrm("file:///etc/passwd\n");
        try
        {
            var size = await service.ResolveAsync(new Video { Path = path, Size = 0 }, RemoteSizeTrigger.ManualIncrementalProbe, CancellationToken.None);

            Assert.Null(size);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Query_SurvivesCdnThatDegradesSingleByteRange()
    {
        const long total = 32_100_303_697L;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            return request.Headers.Range?.ToString() == "bytes=0-1"
                ? PartialContent(0, 1, total)
                : Json(HttpStatusCode.OK, 1);
        });
        using var service = CreateService(handler);

        var size = await service.QueryRemoteSizeAsync("http://93.184.216.34/movie.mkv", CancellationToken.None);

        Assert.Equal(total, size);
        var rangedGet = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Get);
        Assert.Equal("bytes=0-1", rangedGet.Headers.Range?.ToString());
    }

    [Fact]
    public void CachedSizeOnly_NeverTriggersALookupOnCacheMiss()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);
        var item = new Video { Path = "/media/movie.strm", Size = 180 };
        var sources = new List<MediaSourceInfo> { new() { Size = 180 } };

        service.ApplyCachedSizeToSources(item, sources);

        Assert.Empty(handler.Requests);
        Assert.Equal(180, sources[0].Size);
    }

    [Fact]
    public async Task CachedSizeOnly_PatchesDisplayValueAfterAProbe()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);
        var path = WriteStrm("http://93.184.216.34/movie.mkv\n");
        try
        {
            var item = new Video { Path = path, Size = 0 };
            await service.ResolveAsync(item, RemoteSizeTrigger.ManualIncrementalProbe, CancellationToken.None);

            var sources = new List<MediaSourceInfo> { new() { Size = 180 } };
            service.ApplyCachedSizeToSources(item, sources);

            Assert.Single(handler.Requests);
            Assert.Equal(EightMegabytes, sources[0].Size);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Resolve_DoesNothingForUnlistedTrigger()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);
        var path = WriteStrm("http://93.184.216.34/movie.mkv\n");
        try
        {
            var item = new Video { Path = path, Size = 0 };

            var size = await service.ResolveAsync(item, RemoteSizeTrigger.None, CancellationToken.None);

            Assert.Null(size);
            Assert.Empty(handler.Requests);
            Assert.Equal(0, item.Size);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-3)]
    public async Task Resolve_DoesNothingForUndefinedTriggerValue(int raw)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);
        var path = WriteStrm("http://93.184.216.34/movie.mkv\n");
        try
        {
            var item = new Video { Path = path, Size = 0 };

            var size = await service.ResolveAsync(item, (RemoteSizeTrigger)raw, CancellationToken.None);

            Assert.Null(size);
            Assert.Empty(handler.Requests);
            Assert.Equal(0, item.Size);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(RemoteSizeTrigger.ItemAdded)]
    [InlineData(RemoteSizeTrigger.PlaybackProbe)]
    [InlineData(RemoteSizeTrigger.ManualIncrementalProbe)]
    public async Task Resolve_AllowedTriggerFillsMissingSize(RemoteSizeTrigger trigger)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);
        var path = WriteStrm("http://93.184.216.34/movie.mkv\n");
        try
        {
            var size = await service.ResolveAsync(
                new Video { Path = path, Size = 0 },
                trigger,
                CancellationToken.None);

            Assert.Equal(EightMegabytes, size);
            Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Head, handler.Requests[0].Method);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Resolve_FullProbeRefreshesAnAlreadyPlausibleSize()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, 10L * 1024 * 1024));
        using var service = CreateService(handler);
        var path = WriteStrm("http://93.184.216.34/movie.mkv\n");
        try
        {
            var item = new Video { Path = path, Size = EightMegabytes };

            var size = await service.ResolveAsync(item, RemoteSizeTrigger.ManualFullProbe, CancellationToken.None);

            Assert.Equal(10L * 1024 * 1024, size);
            Assert.Single(handler.Requests);

        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(RemoteSizeTrigger.ItemAdded)]
    [InlineData(RemoteSizeTrigger.PlaybackProbe)]
    [InlineData(RemoteSizeTrigger.ManualIncrementalProbe)]
    public async Task Resolve_NonFullTriggerNeverOverwritesAnAlreadyPlausibleSize(RemoteSizeTrigger trigger)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, 10L * 1024 * 1024));
        using var service = CreateService(handler);
        var path = WriteStrm("http://93.184.216.34/movie.mkv\n");
        try
        {
            var item = new Video { Path = path, Size = EightMegabytes };

            var size = await service.ResolveAsync(item, trigger, CancellationToken.None);

            Assert.Null(size);
            Assert.Empty(handler.Requests);
            Assert.Equal(EightMegabytes, item.Size);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Diagnostics_ExplainRejectedHeadAndIgnoredRange()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            : Json(HttpStatusCode.OK, 1));
        using var service = CreateService(handler);

        var (size, diagnostic) = await service.QueryRemoteSizeWithDiagnosticsAsync(
            "http://93.184.216.34/movie.mkv",
            CancellationToken.None);

        Assert.Null(size);
        Assert.Contains("405", diagnostic);
        Assert.Contains("Content-Range", diagnostic);
        Assert.Contains("Content-Length 1", diagnostic);
    }

    [Fact]
    public async Task Diagnostics_ReportTargetPolicyRejection()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler, allowPrivateTargets: false);

        var (size, diagnostic) = await service.QueryRemoteSizeWithDiagnosticsAsync(
            "http://127.0.0.1:8096/movie.mkv",
            CancellationToken.None);

        Assert.Null(size);
        Assert.Contains("allowed-target", diagnostic);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Diagnostics_ReportTheHopThatSucceeded()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);

        var (size, diagnostic) = await service.QueryRemoteSizeWithDiagnosticsAsync(
            "http://93.184.216.34/movie.mkv",
            CancellationToken.None);

        Assert.Equal(EightMegabytes, size);
        Assert.Equal("ok (HEAD)", diagnostic);
    }

    [Fact]
    public void DescribeTransport_NeverLeaksProxyCredentials()
    {
        var transport = RemoteSizeService.DescribeTransport();

        Assert.False(string.IsNullOrWhiteSpace(transport));
        Assert.DoesNotContain("@", transport);
        Assert.True(
            transport.StartsWith("directly", StringComparison.Ordinal)
            || transport.StartsWith("through proxy", StringComparison.Ordinal),
            transport);
    }

    private static RemoteSizeService CreateService(HttpMessageHandler handler, bool allowPrivateTargets = true)
        => new(null!, NullLogger<RemoteSizeService>.Instance, handler, allowPrivateTargets);

    private static string WriteStrm(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"faststrm-{Guid.NewGuid():N}.strm");
        File.WriteAllText(path, content);
        return path;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, long contentLength)
    {
        var content = new StringContent(string.Empty);
        content.Headers.ContentLength = contentLength;
        return new HttpResponseMessage(status) { Content = content };
    }

    private static HttpResponseMessage PartialContent(long from, long to, long total)
    {
        var content = new ByteArrayContent(new byte[] { 0 });
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }

    private static HttpResponseMessage Html(HttpStatusCode status, long contentLength, long? total = null)
    {
        var content = new StringContent("<html><body>error</body></html>", System.Text.Encoding.UTF8, "text/html");
        content.Headers.ContentLength = contentLength;
        if (total is long rangeTotal)
        {
            content.Headers.ContentRange = new ContentRangeHeaderValue(0, contentLength - 1, rangeTotal);
        }

        return new HttpResponseMessage(status) { Content = content };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
