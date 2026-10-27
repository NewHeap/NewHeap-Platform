using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// What a remote MCP server can make the host hold is bounded before NewHeap's own limits apply: a
/// response, or each event of an event stream, by <see cref="NhAssistantMcpOptions.MaxResponseBytes"/>,
/// read only up to that bound, and <c>tools/list</c> by <see cref="NhAssistantMcpOptions.MaxToolListPages"/>
/// and <see cref="NhAssistantMcpOptions.MaxTools"/>, for turns, tests and syncs alike, with stable codes.
/// The tests with persistence run on both providers.
/// </summary>
public sealed class AssistantMcpBoundsTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string Admin = "admin-1";
    private const string ServerId = "planning";
    private const string ServerUrl = "http://localhost/planning/mcp";
    private const int Bound = 64 * 1024;

    [Fact]
    public async Task A_response_within_the_bound_reads_whole_with_its_headers()
    {
        var body = new string('a', Bound);
        using var client = BoundedClient(_ => Json(body));

        using var response = await client.GetAsync("http://localhost/mcp", CancellationToken.None);
        var read = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(body, read);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_response_longer_than_the_bound_stops_reading_just_past_it()
    {
        var source = new CountingStream(Encoding.UTF8.GetBytes(new string('a', Bound * 8)));
        using var client = BoundedClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(source) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }
        });

        var failure = await Record.ExceptionAsync(() => client.GetStringAsync("http://localhost/mcp", CancellationToken.None));

        Assert.NotNull(failure);
        Assert.Equal(NhAssistantAdminErrorCodes.McpResponseTooLarge, NhAssistantMcpFailures.Classify(failure));
        // The host read the bound and at most one more buffer, never the whole response.
        Assert.InRange(source.BytesRead, Bound + 1, Bound * 3);
    }

    [Fact]
    public async Task A_declared_length_beyond_the_bound_fails_before_the_body_is_read()
    {
        var source = new CountingStream(new byte[Bound + 1]);
        using var client = BoundedClient(_ =>
        {
            var content = new StreamContent(source);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentLength = Bound + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var failure = await Record.ExceptionAsync(
            () => client.GetAsync("http://localhost/mcp", HttpCompletionOption.ResponseHeadersRead, CancellationToken.None));

        Assert.NotNull(failure);
        Assert.Equal(NhAssistantAdminErrorCodes.McpResponseTooLarge, NhAssistantMcpFailures.Classify(failure));
        Assert.Equal(0, source.BytesRead);
    }

    [Fact]
    public async Task An_event_stream_bounds_each_event_and_stays_open_for_many_small_ones()
    {
        // A hundred events of a kilobyte, together longer than the bound, then one event beyond it.
        var small = string.Concat(Enumerable.Range(0, 100).Select(index => "data: " + new string('p', 1_000) + "\r\n\r\n"));
        var large = "data: " + new string('r', Bound) + "\n\n";
        using var client = BoundedClient(request => EventStream(request.RequestUri!.AbsolutePath == "/small" ? small : small + large));

        var read = await client.GetStringAsync("http://localhost/small", CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => client.GetStringAsync("http://localhost/large", CancellationToken.None));

        Assert.Equal(small, read);
        Assert.NotNull(failure);
        Assert.Equal(NhAssistantAdminErrorCodes.McpResponseTooLarge, NhAssistantMcpFailures.Classify(failure));
    }

    /// <summary>
    /// Over real HTTP, a tool result whose event is longer than the bound fails its call with
    /// <c>assistant-mcp-response-too-large</c> while the host reads no more of it; the tool list and a
    /// small result read as before.
    /// </summary>
    [Fact]
    public async Task A_tool_result_longer_than_the_bound_fails_its_call_over_http()
    {
        await using var remote = await HttpMcpTestServer.StartAsync(() =>
        [
            McpServerTool.Create(() => new string('r', Bound * 4), new McpServerToolCreateOptions { Name = "large_result", ReadOnly = true }),
            McpServerTool.Create(() => "small", new McpServerToolCreateOptions { Name = "small_result", ReadOnly = true })
        ]);
        var options = Options(mcp => mcp.MaxResponseBytes = Bound);
        var factory = new NhAssistantHttpMcpClientFactory(new NhAssistantMcpHostGuard(options, Development()), options);
        var plan = new NhAssistantMcpConnectionPlan(ServerId, new Uri(remote.StreamableUrl), new Dictionary<string, string>(), false);

        await using var client = await factory.ConnectAsync(plan, CancellationToken.None);
        var tools = await NhAssistantMcpToolLists.ListAsync(client, options.CurrentValue.Mcp, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => client.CallToolAsync("large_result", cancellationToken: CancellationToken.None).AsTask());

        Assert.Equal(["large_result", "small_result"], tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.NotNull(failure);
        Assert.Equal(NhAssistantAdminErrorCodes.McpResponseTooLarge, NhAssistantMcpFailures.Classify(failure));
    }

    /// <summary>
    /// A server that lists more tools than <see cref="NhAssistantMcpOptions.MaxTools"/> is not
    /// connected: the test reports <c>assistant-mcp-tool-list-too-large</c>, a sync fails with it and
    /// stores none of the tools, and a turn skips the server.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_server_that_lists_more_tools_than_the_host_takes_is_refused_and_a_sync_stores_none(AssistantTestProvider provider)
    {
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(provider, servers, mcp => mcp.MaxTools = 1);
        Assert.True((await McpAsync(host, mcp => mcp.CreateAsync(Server(), Admin))).Success);

        var tested = await McpAsync(host, mcp => mcp.TestAsync(ServerId));
        var synced = await McpAsync(host, mcp => mcp.SyncAsync(ServerId, Admin));
        var stored = await McpAsync(host, mcp => mcp.ListToolsAsync(ServerId));

        Assert.Equal((false, NhAssistantAdminErrorCodes.McpToolListTooLarge), (tested.Data!.Ok, tested.Data.Code));
        Assert.Equal(NhAssistantAdminErrorCodes.McpToolListTooLarge, NhAssistantResults.CodeOf(synced));
        Assert.Empty(stored.Data!);
    }

    /// <summary>
    /// A server whose <c>tools/list</c> announces a next page without end is asked for no more than
    /// <see cref="NhAssistantMcpOptions.MaxToolListPages"/> pages and refused, while a list of as many
    /// pages is read whole and synced.
    /// </summary>
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_tool_list_is_read_page_by_page_up_to_its_page_bound(AssistantTestProvider provider)
    {
        var endless = new PagedServer(pages: null);
        var three = new PagedServer(pages: 3);
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, endless.Handlers);
        servers.Register("http://localhost/paged/mcp", three.Handlers);
        await using var host = await CreateHostAsync(provider, servers, mcp => mcp.MaxToolListPages = 3);
        Assert.True((await McpAsync(host, mcp => mcp.CreateAsync(Server(), Admin))).Success);
        Assert.True((await McpAsync(host, mcp => mcp.CreateAsync(Server() with { Id = "paged", Url = "http://localhost/paged/mcp" }, Admin))).Success);

        var refused = await McpAsync(host, mcp => mcp.TestAsync(ServerId));
        var synced = await McpAsync(host, mcp => mcp.SyncAsync("paged", Admin));

        Assert.Equal((false, NhAssistantAdminErrorCodes.McpToolListTooLarge), (refused.Data!.Ok, refused.Data.Code));
        Assert.Equal(3, endless.Pages);
        Assert.True(synced.Success, string.Join(", ", synced.AllErrorMessages));
        Assert.Equal(["tool-1", "tool-2", "tool-3"], synced.Data!.Select(tool => tool.RemoteName).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("MaxResponseBytes", 1_024)]
    [InlineData("MaxResponseBytes", 512 * 1024 * 1024)]
    [InlineData("MaxToolListPages", 0)]
    [InlineData("MaxToolListPages", 1_001)]
    [InlineData("MaxTools", 0)]
    [InlineData("MaxTools", 10_001)]
    public async Task Startup_fails_for_a_bound_out_of_its_range(string option, int value)
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql, new InMemoryMcpServers(), mcp =>
        {
            switch (option)
            {
                case "MaxResponseBytes":
                    mcp.MaxResponseBytes = value;
                    break;
                case "MaxToolListPages":
                    mcp.MaxToolListPages = value;
                    break;
                default:
                    mcp.MaxTools = value;
                    break;
            }
        });
        var validator = host.Services.GetServices<IHostedService>().Single(service => service.GetType().Name == "NhAssistantStartupValidator");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains(option, failure.Message, StringComparison.Ordinal);
    }

    private static HttpClient BoundedClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        return new HttpClient(new NhAssistantMcpResponseLimitHandler(Bound) { InnerHandler = new RespondingHandler(respond) });
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage EventStream(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }

    private static IOptionsMonitor<NhAssistantOptions> Options(Action<NhAssistantMcpOptions> configure)
    {
        return new ServiceCollection()
            .Configure<NhAssistantOptions>(options => configure(options.Mcp))
            .BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<NhAssistantOptions>>();
    }

    private static IHostEnvironment Development()
    {
        return new Microsoft.Extensions.Hosting.Internal.HostingEnvironment
        {
            EnvironmentName = Environments.Development,
            ApplicationName = "assistant-tests",
            ContentRootPath = AppContext.BaseDirectory
        };
    }

    private static NhAssistantMcpServerRequest Server()
    {
        return new NhAssistantMcpServerRequest(ServerId, "Planning", ServerUrl, NhAssistantMcpAuthModes.None, null, null, null, true);
    }

    private Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        InMemoryMcpServers servers,
        Action<NhAssistantMcpOptions> mcp)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services => services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers)),
            assistantBuilder: assistant => assistant.ConfigureMcp(mcp));
    }

    private static async Task<T> McpAsync<T>(AssistantTestHost host, Func<INhAssistantMcpServers, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, Admin);
        return await action(scope.ServiceProvider.GetRequiredService<INhAssistantMcpServers>());
    }

    /// <summary>
    /// A server that lists one tool per page: <c>tool-1</c>, <c>tool-2</c> and so on, with a next page
    /// after each up to <c>pages</c>, or without end.
    /// </summary>
    private sealed class PagedServer(int? pages)
    {
        private int _pages;

        public int Pages => _pages;

        public McpServerHandlers Handlers => new()
        {
            ListToolsHandler = (request, _) =>
            {
                var page = int.TryParse(request.Params?.Cursor, out var cursor) ? cursor : 1;
                Interlocked.Increment(ref _pages);
                return ValueTask.FromResult(new ListToolsResult
                {
                    Tools =
                    [
                        new Tool
                        {
                            Name = "tool-" + page,
                            Description = "Tool " + page + ".",
                            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" })
                        }
                    ],
                    NextCursor = pages is { } last && page >= last ? null : (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            },
            CallToolHandler = (_, _) => ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "Done." }] })
        };
    }

    private sealed class RespondingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>
    /// A response body that counts how much of it was read. It cannot seek, as a network stream, so a
    /// response over it declares no length of its own.
    /// </summary>
    private sealed class CountingStream(byte[] content) : MemoryStream(content, writable: false)
    {
        private long _read;

        public long BytesRead => Interlocked.Read(ref _read);

        public override bool CanSeek => false;

        /// <summary>
        /// Every read of a memory stream that is not one itself ends here, so each byte counts once.
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            Interlocked.Add(ref _read, read);
            return read;
        }
    }
}
