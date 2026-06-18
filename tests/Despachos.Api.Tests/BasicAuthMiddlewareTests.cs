using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Despachos.Api.Tests;

public class BasicAuthMiddlewareTests : IDisposable
{
    private readonly TestServer _serverNoCreds;
    private readonly TestServer _serverWithCreds;
    private readonly HttpClient _clientNoCreds;
    private readonly HttpClient _clientWithCreds;

    public BasicAuthMiddlewareTests()
    {
        _serverNoCreds = CreateServer(null, null);
        _clientNoCreds = _serverNoCreds.CreateClient();

        _serverWithCreds = CreateServer("sapuser", "sappass");
        _clientWithCreds = _serverWithCreds.CreateClient();
    }

    [Fact]
    public async Task SinHeaderAuthorization_ConCredsConfiguradas_Retorna401()
    {
        var resp = await _clientWithCreds.GetAsync("/test");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("Basic", resp.Headers.WwwAuthenticate.First().Scheme);
    }

    [Fact]
    public async Task CredencialesIncorrectas_Retorna401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/test");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Base64("bad:user"));

        var resp = await _clientWithCreds.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CredencialesCorrectas_Retorna200()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/test");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Base64("sapuser:sappass"));

        var resp = await _clientWithCreds.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("pong", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HeaderAuthorizationMalFormado_Retorna401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/test");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", "no-es-base64-valido!!!");

        var resp = await _clientWithCreds.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task HeaderSinPrefijoBasic_Retorna401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/test");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Base64("sapuser:sappass"));

        var resp = await _clientWithCreds.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_BypassAuth_Retorna200()
    {
        var resp = await _clientWithCreds.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task HealthReadyEndpoint_BypassAuth_Retorna200()
    {
        var resp = await _clientWithCreds.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task SinCredsConfiguradas_DejaPasarSinAuth()
    {
        var resp = await _clientNoCreds.GetAsync("/test");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static TestServer CreateServer(string? user, string? pass)
    {
        var builder = new WebHostBuilder()
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                var dict = new Dictionary<string, string?>();
                if (user is not null) dict["SapInbound:Username"] = user;
                if (pass is not null) dict["SapInbound:Password"] = pass;
                cfg.AddInMemoryCollection(dict);
            })
            .ConfigureServices(services =>
            {
            })
            .Configure(app =>
            {
                app.UseMiddleware<Despachos.Api.Middleware.BasicAuthMiddleware>();
                app.Map("/test", branch => branch.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.WriteAsync("pong");
                }));
                app.Map("/health", branch => branch.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.WriteAsync("ok");
                }));
                app.Map("/health/ready", branch => branch.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.WriteAsync("ok");
                }));
            });

        return new TestServer(builder);
    }

    private static string Base64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    public void Dispose()
    {
        _clientNoCreds.Dispose();
        _clientWithCreds.Dispose();
        _serverNoCreds.Dispose();
        _serverWithCreds.Dispose();
    }
}
