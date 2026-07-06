using System.Net;
using AeroDesk.Core.Connections;
using Xunit;

namespace AeroDesk.Core.Tests;

public sealed class DfHttpClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new();
        public List<HttpRequestMessage> Requests { get; } = [];

        public StubHandler Map(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes[pathAndQuery] = (status, body);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var key = request.RequestUri!.PathAndQuery;
            if (!_routes.TryGetValue(key, out var route))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($$"""{"error":"no stub for {{key}}"}"""),
                });
            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body),
            });
        }
    }

    private static DfHttpClient Client(StubHandler handler, string? apiKey = null) => new(
        new DfConnectionDescriptor { Name = "test", Url = "http://test-node:5001", Database = "airline" },
        apiKey, handler);

    [Fact]
    public async Task Connect_Checks_Health_Then_Databases()
    {
        var handler = new StubHandler()
            .Map("/health", """{"status":"ok","version":"1.2.3"}""")
            .Map("/databases", """{"databases":[{"name":"airline","isDefault":false}]}""");
        await using var client = Client(handler);

        await client.ConnectAsync();

        Assert.True(client.IsConnected);
        Assert.Equal(["/health", "/databases"], handler.Requests.Select(r => r.RequestUri!.PathAndQuery));
    }

    [Fact]
    public async Task ApiKey_Is_Sent_As_Bearer()
    {
        var handler = new StubHandler().Map("/health", """{"status":"ok"}""");
        await using var client = Client(handler, apiKey: "sk-test");

        await client.GetHealthAsync();

        var auth = handler.Requests.Single().Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("sk-test", auth.Parameter);
    }

    [Fact]
    public async Task Execute_Parses_Documents_And_Preserves_Raw_Json()
    {
        var handler = new StubHandler().Map("/db/airline/query",
            """{"success":true,"documents":[{"_id":"abc","_etag":"e1","orderId":"AT-1"}],"affected":1}""");
        await using var client = Client(handler);

        var result = await client.ExecuteAsync("SELECT * FROM orders");

        Assert.True(result.Success);
        var doc = Assert.Single(result.Documents);
        Assert.Contains("\"_etag\":\"e1\"", doc);
        Assert.Equal(1, result.AffectedCount);
    }

    [Fact]
    public async Task Update_Sends_IfMatch_And_Returns_New_Etag()
    {
        var handler = new StubHandler().Map("/db/airline/collections/orders/doc1",
            """{"success":true,"etag":"e2"}""");
        await using var client = Client(handler);

        var etag = await client.UpdateDocumentAsync("orders", "doc1", """{"a":1}""", "e1");

        Assert.Equal("e2", etag);
        var request = handler.Requests.Single();
        Assert.Equal("e1", request.Headers.GetValues("If-Match").Single());
    }

    [Fact]
    public async Task Update_412_Throws_EtagConflict_With_Both_Etags()
    {
        var handler = new StubHandler().Map("/db/airline/collections/orders/doc1",
            """{"error":"etag mismatch","expected":"e1","actual":"e9"}""",
            HttpStatusCode.PreconditionFailed);
        await using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<EtagConflictException>(() =>
            client.UpdateDocumentAsync("orders", "doc1", """{"a":1}""", "e1"));

        Assert.Equal("e1", ex.ExpectedEtag);
        Assert.Equal("e9", ex.ActualEtag);
    }

    [Fact]
    public async Task GetDocument_Returns_Null_On_404()
    {
        var handler = new StubHandler(); // nothing mapped -> 404
        await using var client = Client(handler);

        Assert.Null(await client.GetDocumentAsync("orders", "missing"));
    }

    [Fact]
    public async Task Server_Error_Message_Is_Extracted()
    {
        var handler = new StubHandler().Map("/db/airline/query",
            """{"error":"syntax error near FORM"}""", HttpStatusCode.BadRequest);
        await using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<DfHttpException>(() => client.ExecuteAsync("SELECT * FORM x"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("syntax error near FORM", ex.Message);
    }
}
