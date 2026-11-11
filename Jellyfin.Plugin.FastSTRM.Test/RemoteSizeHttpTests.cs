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

/// <summary>
/// 远端大小解析的 HTTP 链路：目标过滤、HEAD → Range 回退、URL 缓存复用。
/// </summary>
/// <remarks>
/// <see cref="RemoteSizeService"/> 的传输层通过 internal 构造注入，因此这些用例不发真实请求。
/// PersistAsync 依赖 Jellyfin 的库管理器，这里传 null：写库失败本身就是被 catch 的
/// （ResolveAsync 的契约是「永不抛异常」），顺带也验证了这条契约。
/// </remarks>
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
        // 默认放行私网/环回：局域网媒体服务器是本插件的主场景，安全开关按需收紧。
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
        Assert.Equal("bytes=0-0", handler.Requests[1].Headers.Range?.ToString());
    }

    [Fact]
    public async Task Query_RejectsHtmlErrorPage()
    {
        // 200 + 900 字节的 HTML 错误页：既不是 HEAD 可用的长度，也不是合法媒体大小。
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, 900));
        using var service = CreateService(handler);

        var size = await service.QueryRemoteSizeAsync("http://93.184.216.34/movie.mkv", CancellationToken.None);

        Assert.Null(size);
    }

    [Fact]
    public async Task Query_AcceptsSmallButPlausibleRemoteSize()
    {
        // 短片/音乐视频本身就可能是几百 KB：1 MiB 的旧阈值会让这类条目永远补不上。
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

            // 同一个 URL 在同一次运行里只请求一次；force 只影响写库，不影响已解析的地址。
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
            // 库里的值（500 KB）与 .strm 文本长度（几十字节）不同 → 上一轮已经写过的真实大小。
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

    // ── 取源 / 浏览详情：只读缓存，绝不触发补全 ──

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

    // ── 触发来源准入：未登记的场景连 .strm 都不读、不发请求、不写库 ──

    [Fact]
    public async Task Resolve_DoesNothingForUnlistedTrigger()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);
        var path = WriteStrm("http://93.184.216.34/movie.mkv\n");
        try
        {
            // None = 官方探测 / 不探测 / 取源 / 详情页 / 统计等所有未列明场景。
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
        // 四个允许场景（全量探测另有用例）：缺大小的条目必须真的发一次请求并拿到长度。
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, EightMegabytes));
        using var service = CreateService(handler);
        var path = WriteStrm("http://93.184.216.34/movie.mkv\n");
        try
        {
            var size = await service.ResolveAsync(
                new Video { Path = path, Size = 180 },
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
        // 全量探测的契约是「每个字段都用新数据覆盖」：已有 8 MiB 的可信大小也要重新解析。
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, 10L * 1024 * 1024));
        using var service = CreateService(handler);
        var path = WriteStrm("http://93.184.216.34/movie.mkv\n");
        try
        {
            var item = new Video { Path = path, Size = EightMegabytes };

            var size = await service.ResolveAsync(item, RemoteSizeTrigger.ManualFullProbe, CancellationToken.None);

            Assert.Equal(10L * 1024 * 1024, size);
            Assert.Single(handler.Requests);

            // item.Size 不在这里断言：写库需要 ILibraryManager（本用例传 null，写库失败会回滚内存值），
            // 「全量覆盖」的写入语义由 ResolveAsync 的返回值 + RemoteSizeService.PersistAsync 的 force 分支保证。
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
        // 非全量来源只补「仍然缺失」的大小，绝不覆盖已有记录，也不为此发请求。
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

    // ── helpers ──

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
