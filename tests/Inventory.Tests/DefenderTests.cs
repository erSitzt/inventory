using System.Net;
using System.Text;
using Inventory.Core.Abstractions;
using Inventory.Remoting;

namespace Inventory.Tests;

public class DefenderTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";

    [Theory]
    [InlineData(Tenant, Client, true)]
    [InlineData("contoso.onmicrosoft.com", Client, true)]
    [InlineData("contoso.com/evil?x=", Client, false)]
    [InlineData("", Client, false)]
    [InlineData(null, Client, false)]
    [InlineData(Tenant, "not-a-guid", false)]
    public void Tenant_and_application_must_be_plain_ids(string? tenant, string? client, bool valid) =>
        Assert.Equal(valid, DefenderClient.IsValid(tenant, client));

    [Fact]
    public async Task Sign_in_waits_until_the_code_was_entered()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, """{"device_code":"dc","user_code":"ABCD-1234","verification_uri":"https://microsoft.com/devicelogin","interval":5,"expires_in":900}"""),
            (HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            (HttpStatusCode.BadRequest, """{"error":"slow_down"}"""),
            (HttpStatusCode.OK, """{"access_token":"tok"}"""));
        var waits = new List<TimeSpan>();
        var defender = new DefenderClient(new HttpClient(handler)) { Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } };

        var code = await defender.BeginSignInAsync(Tenant, Client);
        Assert.Equal(("ABCD-1234", "https://microsoft.com/devicelogin"), (code.UserCode, code.VerificationUri));
        Assert.Equal("tok", await defender.WaitForTokenAsync(Tenant, Client, code));

        Assert.Equal([5, 5, 10], waits.Select(w => (int)w.TotalSeconds));
        Assert.EndsWith($"/{Tenant}/oauth2/v2.0/devicecode", handler.Requests[0].Url);
        Assert.Contains("client_id=" + Client, handler.Requests[0].Body);
        Assert.Contains("device_code=dc", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Declined_or_failed_sign_in_is_reported_readably()
    {
        var code = new DefenderClient.DeviceCode("dc", "X", "https://microsoft.com/devicelogin", 1, DateTime.UtcNow.AddMinutes(5));
        DefenderClient With(string body) => new(new HttpClient(new ScriptedHandler((HttpStatusCode.BadRequest, body)))) { Delay = (_, _) => Task.CompletedTask };

        Assert.Contains("declined", (await Assert.ThrowsAsync<RemoteException>(() => With("""{"error":"authorization_declined"}""").WaitForTokenAsync(Tenant, Client, code))).Message);
        Assert.Contains("expired", (await Assert.ThrowsAsync<RemoteException>(() => With("""{"error":"expired_token"}""").WaitForTokenAsync(Tenant, Client, code))).Message);
        var other = await Assert.ThrowsAsync<RemoteException>(() => With("""{"error":"invalid_client","error_description":"AADSTS7000218: public client flows are disabled.\r\nTrace ID: 1"}""").WaitForTokenAsync(Tenant, Client, code));
        Assert.Contains("public client flows are disabled", other.Message);
        Assert.DoesNotContain("Trace ID", other.Message);

        var start = await Assert.ThrowsAsync<RemoteException>(() => With("""{"error":"unauthorized_client","error_description":"AADSTS700016: application not found"}""").BeginSignInAsync(Tenant, Client));
        Assert.Contains("application not found", start.Message);
        await Assert.ThrowsAsync<RemoteException>(() => With("{}").BeginSignInAsync("a/b", Client));
    }

    [Fact]
    public async Task Allowing_the_runner_creates_the_indicator_and_removes_only_its_own_outdated_ones()
    {
        var current = new string('a', 64);
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, $$"""
                {"value":[
                  {"id":"1","title":"Inventory runner","indicatorType":"FileSha256","action":"Allowed","indicatorValue":"{{current.ToUpperInvariant()}}"},
                  {"id":"2","title":"Inventory runner","indicatorType":"FileSha256","action":"Allowed","indicatorValue":"{{new string('b', 64)}}"},
                  {"id":"3","title":"Inventory runner","indicatorType":"FileSha256","action":"Block","indicatorValue":"{{new string('c', 64)}}"},
                  {"id":"4","title":"Something else","indicatorType":"FileSha256","action":"Allowed","indicatorValue":"{{new string('d', 64)}}"}
                ]}
                """),
            (HttpStatusCode.NoContent, ""));
        var defender = new DefenderClient(new HttpClient(handler));

        var result = await defender.AllowRunnerAsync("tok", current, "runner", removeOutdated: true);

        Assert.Equal(1, result.Removed);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(("POST", "https://api.security.microsoft.com/api/indicators", "Bearer tok"), (handler.Requests[0].Method, handler.Requests[0].Url, handler.Requests[0].Authorization));
        Assert.Contains("\"indicatorValue\":\"" + current, handler.Requests[0].Body);
        Assert.Contains("\"action\":\"Allowed\"", handler.Requests[0].Body);
        Assert.Contains("\"generateAlert\":false", handler.Requests[0].Body);
        Assert.Equal(("DELETE", "https://api.security.microsoft.com/api/indicators/2"), (handler.Requests[2].Method, handler.Requests[2].Url));
    }

    [Fact]
    public async Task Missing_permission_is_explained_and_nothing_is_removed_without_asking()
    {
        var denied = new DefenderClient(new HttpClient(new ScriptedHandler((HttpStatusCode.Forbidden, "{}"))));
        Assert.Contains("Ti.ReadWrite", (await Assert.ThrowsAsync<RemoteException>(() => denied.AllowRunnerAsync("tok", new string('a', 64), "runner", true))).Message);

        var handler = new ScriptedHandler((HttpStatusCode.OK, "{}"));
        Assert.Equal(0, (await new DefenderClient(new HttpClient(handler)).AllowRunnerAsync("tok", new string('a', 64), "runner", removeOutdated: false)).Removed);
        Assert.Single(handler.Requests);
    }

    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<(string Method, string Url, string Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.GetLeftPart(UriPartial.Path), request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString()));
            var (status, body) = responses[Math.Min(_next++, responses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
