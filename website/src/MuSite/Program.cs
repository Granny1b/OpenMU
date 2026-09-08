using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using MuSite;
using MuSite.Auth;
using MuSite.Data;
using MuSite.Live;
using MuSite.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "MUSITE_");
builder.Services.Configure<SiteOptions>(builder.Configuration);

// ---------------------------------------------------------------------------------------------
// Two Kestrel endpoints. 8080 is proxied to the internet; 8081 carries /healthz and
// /healthz/schema, is never published in any compose file and is never proxied. The health payload
// enumerates schema detail, so it gets a port of its own AND a `location /healthz { deny all; }`
// in nginx - two independent controls, because either one alone is a single point of failure.
// ---------------------------------------------------------------------------------------------
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(8080);
    kestrel.ListenAnyIP(8081);
});

// ---------------------------------------------------------------------------------------------
// Forwarded headers. ForwardedHeadersOptions.ForwardedHeaders defaults to None, so
// UseForwardedHeaders() forwards NOTHING unless this is set explicitly. Without it every request
// carries the proxy container's bridge IP and the rate limiters below become one global bucket for
// the entire internet - registration would be a handful per hour for all visitors combined.
// The nginx variant additionally needs proxy_set_header X-Forwarded-For / -Proto / Host added:
// deploy/all-in-one/nginx/*.conf set none of them today. Traefik sets them itself.
// ---------------------------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();

    var trusted = builder.Configuration.GetSection("TrustedNetworks").Get<string[]>() ?? [];
    foreach (var entry in trusted)
    {
        // Fully qualified: Microsoft.AspNetCore.HttpOverrides also defines an IPNetwork, so the bare
        // name is ambiguous with both namespaces in scope.
        if (entry.Contains('/', StringComparison.Ordinal)
            && System.Net.IPNetwork.TryParse(entry, out var network))
        {
            options.KnownNetworks.Add(network);
        }
        else if (IPAddress.TryParse(entry, out var single))
        {
            options.KnownProxies.Add(single);
        }
    }
});

builder.Services.AddSingleton(_ => SiteDataSources.Create(builder.Configuration));
builder.Services.AddSingleton<SchemaContract>();
builder.Services.AddHostedService<SchemaContractService>();
builder.Services.AddSingleton<RoleResolver>();
builder.Services.AddSingleton<PublicQueries>();
builder.Services.AddSingleton<RankingCache>();
builder.Services.AddMemoryCache();
builder.Services.AddOutputCache();

// One instance, resolved both as the hosted service that polls and as the dependency pages read.
builder.Services.AddSingleton<ServerProbe>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerProbe>());

// ---------------------------------------------------------------------------------------------
// The key ring signs the auth cookie. It is written to /app/keys, which the Dockerfile creates as
// mode 700 owned by the app user - NOT the chmod 777 used for the game server's key directory.
// Anything able to read it can forge a session for any account, including the Owner.
// ---------------------------------------------------------------------------------------------
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/app/keys"))
    .SetApplicationName("MuSite");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "musite.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;

        // Phase 2 attaches OnValidatePrincipal here: it re-reads the session row and the account
        // State so a ban, a password change or a role change takes effect on the next request
        // rather than in fourteen days.
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(SitePolicies.Player, policy => policy.RequireAuthenticatedUser())
    .AddPolicy(SitePolicies.Admin, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(SitePolicies.RoleClaim, nameof(SiteRole.Admin), nameof(SiteRole.Owner)))
    .AddPolicy(SitePolicies.Owner, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(SitePolicies.RoleClaim, nameof(SiteRole.Owner)));

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Account", SitePolicies.Player);
    options.Conventions.AuthorizeFolder("/Admin", SitePolicies.Admin);
})
.AddMvcOptions(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

// ---------------------------------------------------------------------------------------------
// Security headers. The CSP carries no 'unsafe-inline' for styles: every dynamic width in the UI
// (the stat bars) is expressed with a step class in mu.css rather than a style attribute.
// ---------------------------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Content-Security-Policy"] =
        "default-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; "
        + "img-src 'self' data:; style-src 'self'; font-src 'self'; connect-src 'self'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "same-origin";
    headers["X-Frame-Options"] = "DENY";
    await next().ConfigureAwait(false);
});

// Re-executes into /not-found so a 404 arrives on a themed page rather than a blank browser default,
// while keeping the 404 status code for crawlers.
app.UseStatusCodePagesWithReExecute("/not-found");

app.UseStaticFiles();
app.UseRouting();
app.UseOutputCache();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// Health lives only on 8081, which no compose file publishes and no proxy forwards. The payload
// enumerates schema detail, so nginx also blocks /healthz on 8080 - two independent controls.
app.MapGet("/healthz", () => Results.Text("ok")).RequireHost("*:8081");
app.MapGet("/healthz/schema", (SchemaContract contract) =>
        contract.IsHealthy
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "failed", failures = contract.Failures }, statusCode: 503))
    .RequireHost("*:8081");

// Fail loudly at startup rather than 500ing on the first page view.
using (var scope = app.Services.CreateScope())
{
    var contract = scope.ServiceProvider.GetRequiredService<SchemaContract>();
    if (!await contract.CheckAsync().ConfigureAwait(false))
    {
        app.Logger.LogWarning(
            "Starting with a FAILING schema contract - public pages will show the maintenance notice. "
            + "See /healthz/schema on port 8081. After a -reinit, re-run website/db/01b-grants.sql.");
    }
}

await app.RunAsync().ConfigureAwait(false);
