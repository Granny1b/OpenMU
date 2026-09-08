using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using MuSite;
using MuSite.Auth;
using MuSite.Data;
using MuSite.Game;
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

    // KnownIPNetworks, not KnownNetworks. There are two IPNetwork types in scope, and the obsolete
    // KnownNetworks collection holds Microsoft.AspNetCore.HttpOverrides.IPNetwork while
    // System.Net.IPNetwork.TryParse below produces the framework one - mixing them is a compile
    // error, not a silent mismatch. KnownIPNetworks takes System.Net.IPNetwork and is the
    // replacement the ASPDEPR005 deprecation points at.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();

    var trusted = builder.Configuration.GetSection("TrustedNetworks").Get<string[]>() ?? [];
    foreach (var entry in trusted)
    {
        if (entry.Contains('/', StringComparison.Ordinal)
            && System.Net.IPNetwork.TryParse(entry, out var network))
        {
            options.KnownIPNetworks.Add(network);
        }
        else if (IPAddress.TryParse(entry, out var single))
        {
            options.KnownProxies.Add(single);
        }
    }
});

// Must run before the first query: Dapper caches a deserializer per (type, column shape) the first
// time it materialises one, and consults the handler registry while building it.
Dapper.SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());

builder.Services.AddSingleton(_ => SiteDataSources.Create(builder.Configuration));
builder.Services.AddSingleton<SchemaContract>();
builder.Services.AddHostedService<SchemaContractService>();
builder.Services.AddSingleton<RoleResolver>();
builder.Services.AddSingleton<PublicQueries>();
builder.Services.AddSingleton<RankingCache>();
builder.Services.AddSingleton<GameAccount>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<SessionState>();
builder.Services.AddSingleton<SiteSettings>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<RegistrationThrottle>();
builder.Services.AddSingleton<LoginAttempts>();
builder.Services.AddSingleton<AdminActions>();
builder.Services.AddSingleton<StepUp>();
builder.Services.AddSingleton<NewsStore>();
builder.Services.AddHostedService<SessionPurgeService>();
builder.Services.AddHostedService<BanExpiryService>();
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

        // Re-resolve the session on EVERY request (cached 60s per session id). The cookie carries a
        // session id and nothing else, so a ban, a sign-out elsewhere, a password change or a role
        // change takes effect on the next request instead of whenever the cookie expires.
        options.Events.OnValidatePrincipal = async context =>
        {
            var claim = context.Principal?.FindFirst(SitePolicies.SessionIdClaim)?.Value;
            if (!Guid.TryParse(claim, out var sessionId))
            {
                context.RejectPrincipal();
                return;
            }

            var state = context.HttpContext.RequestServices.GetRequiredService<SessionState>();
            var resolved = await state.ResolveAsync(sessionId, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (!resolved.IsValid)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                    .ConfigureAwait(false);
                return;
            }

            // The role lives in a claim for the authorization policies to read, so a role that
            // changed since sign-in has to be written back - otherwise a demoted admin keeps /admin
            // until their cookie expires.
            if (context.Principal?.FindFirst(SitePolicies.RoleClaim)?.Value != resolved.Role.ToString())
            {
                context.ReplacePrincipal(SitePrincipal.Build(resolved.AccountId, resolved.LoginName, sessionId, resolved.Role));
                context.ShouldRenew = true;
            }
        };
    });

// Per-IP limits. These are worth nothing unless the proxy actually forwards the client address:
// see the ForwardedHeaders configuration above and the proxy_set_header block in
// deploy/all-in-one/nginx/*.conf. With neither, every request on Earth shares one partition.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.AddPolicy(RateLimitPolicies.Register, context => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition.For(context.Connection.RemoteIpAddress),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));

    limiter.AddPolicy(RateLimitPolicies.Login, context => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition.For(context.Connection.RemoteIpAddress),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
});

// AuthorizeFolder below names these policies as STRINGS, and ASP.NET resolves the name per request
// rather than at startup. Without this call every page under /account and /admin throws
// "The AuthorizationPolicy named: 'Site.Player' was not found" while every public page keeps
// serving normally. MuSite.Tests.AuthorizationPolicyTests asserts every name in SitePolicies.All
// resolves.
builder.Services.AddAuthorization(SitePolicies.Configure);

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

// Re-executes into /status so an error arrives on a themed page rather than a blank browser default,
// while keeping the original status code for crawlers.
//
// The code is PASSED ON. Pointing every status at a single "not found" page told a rate-limited
// visitor (429), one whose anti-forgery token had expired (400) and one who was forbidden (403)
// that the page did not exist - three different problems, all reported as the one thing that was
// not true, with nothing to act on.
app.UseStatusCodePagesWithReExecute("/status", "?code={0}");

app.UseStaticFiles();
app.UseRouting();
app.UseOutputCache();
app.UseRateLimiter();
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
