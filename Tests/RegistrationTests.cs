using System.Net;
using Xunit;

namespace OpenTrade.Tests;

public class RegistrationTests : IClassFixture<ApiFactory>
{
    readonly ApiFactory _f;
    public RegistrationTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Anyone_can_create_a_read_only_account_and_is_signed_in()
    {
        var anon = Session.Anonymous(_f);
        var email = $"teacher{Guid.NewGuid().ToString("N")[..6]}@example.com";
        var r = await anon.Post("/api/register", new { email, name = "Professor", password = "a-long-password-1" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("viewer", (await Session.Json(r)).GetProperty("role").GetString());
        Assert.Equal(email, (await anon.Get("/api/me")).GetProperty("email").GetString());     // signed in right away

        // read-only: it can look, not trade
        Assert.Equal(HttpStatusCode.OK, (await anon.Http.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.Post("/api/orders", new { brokerId = "x", symbol = "AAPL", side = "buy", type = "market", qty = 1 })).StatusCode);

        // and it can sign in later with the same credentials
        Assert.Equal(HttpStatusCode.OK, (await Session.Anonymous(_f).Post("/api/login", new { email, password = "a-long-password-1" })).StatusCode);
    }

    [Fact]
    public async Task Registration_validates_input_and_refuses_duplicates()
    {
        var anon = Session.Anonymous(_f);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/register", new { email = "not-an-email", name = "A", password = "a-long-password-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/register", new { email = "a@b.com", name = "", password = "a-long-password-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/register", new { email = "a@b.com", name = "A", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await anon.Post("/api/register", new { email = ApiFactory.Email, name = "Evil", password = "a-long-password-1" })).StatusCode);
        // the setup endpoint tells the page whether to offer "Create account"
        var setup = await anon.Get("/api/setup");
        Assert.False(setup.GetProperty("firstRun").GetBoolean());
        Assert.True(setup.GetProperty("registration").GetBoolean());
    }
}

/// <summary>A fresh install with no admin in the configuration: nothing is pre-created and the first account becomes the admin.</summary>
public class FirstRunTests : IClassFixture<EmptyFactory>
{
    readonly EmptyFactory _f;
    public FirstRunTests(EmptyFactory f) => _f = f;

    [Fact]
    public async Task The_first_registered_account_is_the_admin_and_the_next_ones_are_viewers()
    {
        var anon = Session.Anonymous(_f);
        Assert.True((await anon.Get("/api/setup")).GetProperty("firstRun").GetBoolean());

        var first = await anon.Post("/api/register", new { email = "owner@example.com", name = "Owner", password = "a-long-password-1" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("admin", (await Session.Json(first)).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await anon.Http.GetAsync("/api/users")).StatusCode);     // admin-only endpoint

        Assert.False((await anon.Get("/api/setup")).GetProperty("firstRun").GetBoolean());
        var second = Session.Anonymous(_f);
        var r = await second.Post("/api/register", new { email = "guest@example.com", name = "Guest", password = "a-long-password-1" });
        Assert.Equal("viewer", (await Session.Json(r)).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await second.Http.GetAsync("/api/users")).StatusCode);
    }
}
