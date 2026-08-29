using System.Security.Claims;
using System.Text;
using DigitalBoxApi.Data;
using DigitalBoxApi.Entities;
using DigitalBoxApi.Realtime;
using DigitalBoxApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

const string AppCorsPolicy = "AppCorsPolicy";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

var jwtSection = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        var key = jwtSection["Key"];
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = string.IsNullOrEmpty(key)
                ? null
                : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
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

builder.Services.AddAuthorization();

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
