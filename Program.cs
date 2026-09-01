using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using DigitalBoxApi.Data;
using DigitalBoxApi.Entities;
using DigitalBoxApi.Realtime;
using DigitalBoxApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

const string AppCorsPolicy = "AppCorsPolicy";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// --- Reverse-proxy awareness -------------------------------------------------
// In production Caddy terminates TLS and forwards to this app over plain HTTP. Without honoring
// X-Forwarded-For every request appears to originate from the proxy, so the per-IP login lockout
// (Services/LoginThrottle) collapses into one global bucket an attacker can use to lock out every
// user — and every security log line records the proxy's address instead of the client's. Trust
// the forwarded headers only from the proxy address(es) below, never unconditionally.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // One proxy hop (Caddy). Bump via ForwardedHeaders__ForwardLimit if another proxy (an ALB,
    // Cloudflare) is ever put in front — and add its egress range to KnownNetworks too.
    options.ForwardLimit = builder.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;
    options.KnownProxies.Clear();
    options.KnownNetworks.Clear();

    // Override in prod via ForwardedHeaders__KnownNetworks (comma/semicolon-separated CIDRs) with
    // the tightest range that covers the proxy. Default: loopback + the default Docker bridge
    // range, which is what Kestrel sees when Caddy proxies to a published container port.
    var configured = builder.Configuration["ForwardedHeaders:KnownNetworks"];
    var cidrs = string.IsNullOrWhiteSpace(configured)
        ? new[] { "127.0.0.0/8", "::1/128", "172.16.0.0/12" }
        : configured.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (var cidr in cidrs)
    {
        var slash = cidr.IndexOf('/');
        if (slash > 0
            && IPAddress.TryParse(cidr[..slash], out var prefix)
            && int.TryParse(cidr[(slash + 1)..], out var length)
            && length >= 0
            && length <= (prefix.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32))
        {
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
        }
        else
        {
            Console.Error.WriteLine($"[startup] Ignoring malformed ForwardedHeaders:KnownNetworks entry '{cidr}'.");
        }
    }
});

// The JWT signing key is the entire strength of HS256 auth: a weak or guessable value lets anyone
// forge a token — including an admin one. Fail fast rather than boot with one.
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"];
const int MinJwtKeyBytes = 32;
if (string.IsNullOrEmpty(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < MinJwtKeyBytes)
{
    throw new InvalidOperationException(
        $"Jwt:Key must be at least {MinJwtKeyBytes} bytes ({MinJwtKeyBytes * 8}-bit) of random data. " +
        "Generate one with `openssl rand -base64 48` and supply it via the Jwt__Key environment variable.");
}
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };

        // Re-check the account on every request: a deactivated user or a password reset
        // rotates User.SecurityStamp, so stale tokens are rejected at once rather than living
        // out their remaining lifetime. One indexed SELECT per authorized request.
        options.Events = new JwtBearerEvents
        {
            // The SignalR JS client can't set an Authorization header on the WebSocket
            // handshake, so it passes the JWT as ?access_token= instead. Pick it up for
            // hub requests only; everything else keeps using the header.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments("/hub"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();

                if (!Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                {
                    context.Fail("Malformed token.");
                    return;
                }

                var account = await db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.IsActive, u.SecurityStamp })
                    .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

                var tokenStamp = principal?.FindFirstValue(JwtTokenService.SecurityStampClaim);
                if (account is null || !account.IsActive || account.SecurityStamp.ToString() != tokenStamp)
                {
                    context.Fail("Account is no longer valid.");
                }
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Fail closed: an endpoint that carries no [Authorize]/[AllowAnonymous] now requires an
    // authenticated user rather than being reachable anonymously. The explicit [AllowAnonymous]
    // on AuthController.Login and HealthController still wins. Belt to the "everything is
    // [Authorize] by default" convention in CLAUDE.md.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Request throttling. UseForwardedHeaders runs first in the pipeline, so every partition here
// keys off the real client IP, not Caddy's. See CLAUDE.md "Fan-out endpoints are rate-limit
// candidates" — this is that limiter.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Blanket per-IP backstop for every endpoint (the fan-out ones especially: list, ship,
    // cancel, priority, notes, reports). Generous enough that a warehouse LAN behind one NAT
    // address doing normal UI work never trips it; low enough to stop a scripted flood.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Upload: a hard concurrency cap, not a rate cap. Parsing several 15 MB PDFs at once is the
    // memory-exhaustion path on the shared 1 GB box; the UI already uploads in sequential
    // 6-file batches, so this just enforces that server-side. Excess requests wait briefly in a
    // short queue, then get 429 + Retry-After.
    options.AddConcurrencyLimiter("upload", opt =>
    {
        opt.PermitLimit = 2;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 4;
    });

    // Login: a per-IP request cap on top of LoginThrottle (which owns the account-lockout 423
    // semantics). Stops rapid scripted guessing before it reaches the handler; loose enough for
    // a morning login rush from one office IP.
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Too many requests. Slow down and try again shortly." }, ct);
    };
});

// Per-user accounts (Entities/User + Controllers/UsersController). Admins are seeded with the
// `create-admin` CLI command below; the app never creates an admin.
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<IPasswordGenerator, PasswordGenerator>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();

builder.Services.AddSingleton<IPackingSlipParser, PdfPigPackingSlipParser>();
builder.Services.AddScoped<IPackingSlipStore, PostgresPackingSlipStore>();
builder.Services.AddScoped<OrderIngestionService>();

// Realtime presence + activity feed (Realtime/PresenceHub, mapped below). SignalR ships in the
// Web shared framework — no package reference. The tracker is process-local (see its remarks).
builder.Services.AddSignalR();
builder.Services.AddSingleton<IPresenceTracker, PresenceTracker>();

var configuredOrigin = builder.Configuration["Cors:AllowedOrigin"];
if (string.IsNullOrWhiteSpace(configuredOrigin) && !builder.Environment.IsDevelopment())
{
    // Outside Development, silently falling back to the localhost dev origin would lock the
    // deployed UI out of the API with no obvious cause. Fail fast instead — same pattern as
    // the Jwt:Key check above.
    throw new InvalidOperationException(
        "Cors:AllowedOrigin must be set outside Development (env Cors__AllowedOrigin) — " +
        "it is the deployed SPA origin the API allows.");
}
var allowedOrigin = string.IsNullOrWhiteSpace(configuredOrigin)
    ? "http://localhost:5173"
    : configuredOrigin;
builder.Services.AddCors(options =>
{
    options.AddPolicy(AppCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials() // required for the SignalR hub's negotiate / SSE fallback
            .WithExposedHeaders("Content-Disposition");
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "DigitalBox API", Version = "v1" });

    var jwtScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter a valid JWT token: Bearer {token}"
    };
    options.AddSecurityDefinition("Bearer", jwtScheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// --- CLI command branches (exit before app.Run) -----------------------------

if (args.Length > 0 && args[0] == "create-admin")
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: dotnet run -- create-admin <username> \"<display name>\" [password]");
        Console.WriteLine("Omit [password] to have a passphrase generated. This is the only way to make an admin.");
        return;
    }

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var generator = scope.ServiceProvider.GetRequiredService<IPasswordGenerator>();

    var normalized = UserService.NormalizeUsername(args[1]);
    if (await db.Users.AnyAsync(u => u.Username == normalized))
    {
        Console.WriteLine($"A user named \"{normalized}\" already exists. Aborting.");
        return;
    }

    var password = args.Length >= 4 ? args[3] : generator.Generate();
    var admin = UserService.NewUser(args[1], args[2], UserRole.Admin, password);
    db.Users.Add(admin);
    await db.SaveChangesAsync();

    Console.WriteLine($"Created admin '{admin.Username}' ({admin.DisplayName}).");
    Console.WriteLine();
    Console.WriteLine($"    Password: {password}");
    Console.WriteLine();
    Console.WriteLine("Hand this to the account owner; they can reset it from the Users screen.");
    return;
}

if (args.Length > 0 && args[0] == "dump-pdf")
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: dotnet run -- dump-pdf <path-to.pdf>");
        return;
    }

    if (args.Contains("--rows"))
    {
        foreach (var line in PdfPigPackingSlipParser.DumpRows(await File.ReadAllBytesAsync(args[1])))
        {
            Console.WriteLine(line);
        }

        return;
    }

    using var scope = app.Services.CreateScope();
    var parser = scope.ServiceProvider.GetRequiredService<IPackingSlipParser>();
    var parsed = parser.Parse(await File.ReadAllBytesAsync(args[1]));
    Console.WriteLine($"Confidence : {parsed.Confidence}");
    Console.WriteLine($"OrderNumber: '{parsed.OrderNumber}'");
    Console.WriteLine($"ShipDate   : {parsed.ShipDate}");
    Console.WriteLine($"Note       : {parsed.Note}");
    Console.WriteLine($"LineItems  : {parsed.LineItems.Count}");
    foreach (var li in parsed.LineItems)
    {
        Console.WriteLine($"  - qty {li.Quantity,-4} sku {li.Sku ?? "-",-16} {li.Title}");
    }

    return;
}

// --- HTTP pipeline ---------------------------------------------------------

// Must run before anything that reads the client address, host, or scheme (exception handler,
// auth, the login throttle, the rate limiter). Rewrites them from the proxy's forwarded headers.
app.UseForwardedHeaders();

// Baseline security headers on every response. No HSTS / HTTPS redirect here — Caddy terminates
// TLS in front and the container only speaks HTTP; Caddy adds HSTS. No Cross-Origin-Resource-Policy
// either: the SPA is a separate site and CORS (single allowed origin) is the real access control.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseRateLimiter();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        if (exception is not null)
        {
            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("GlobalExceptionHandler");
            logger.LogError(exception, "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { message = "An unexpected error occurred." });
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// No HTTPS redirect: dev is plain HTTP and in production Caddy terminates TLS in front of
// the container (which listens on HTTP :8080).
app.UseCors(AppCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<PresenceHub>("/hub/activity");

app.Run();
