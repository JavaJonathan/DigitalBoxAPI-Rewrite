using System.Text;
using DigitalBoxApi.Data;
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
    });

builder.Services.AddAuthorization();

// Single shared credential — no user store. See Services/PasswordHasher + AuthController.
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();

builder.Services.AddSingleton<IPackingSlipParser, PdfPigPackingSlipParser>();
builder.Services.AddScoped<IPackingSlipStore, PostgresPackingSlipStore>();
builder.Services.AddScoped<OrderIngestionService>();

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

if (args.Length > 0 && args[0] == "set-password")
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: dotnet run -- set-password <plaintext>");
        return;
    }

    var hash = PasswordHasher.Hash(args[1]);
    Console.WriteLine("Set this as Auth:PasswordHash (user-secrets locally / SSM in prod):");
    Console.WriteLine();
    Console.WriteLine(hash);
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

app.Run();
