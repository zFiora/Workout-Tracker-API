using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Resend;
using WorkoutTrackerAPI.Data;
using WorkoutTrackerAPI.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
// A DbContext instance can only run one operation at a time, so endpoints that need
// to run independent reads concurrently (e.g. sync/bootstrap) pull short-lived
// contexts from this factory instead of sharing the request-scoped one. The plain
// AppDbContext used everywhere else is then just a scoped instance sourced from the
// same factory — registering AddDbContext separately alongside AddDbContextFactory
// causes a DI lifetime conflict (the factory is a singleton but AddDbContext's
// DbContextOptions is scoped), so this is the one registration for both.
builder.Services.AddDbContextFactory<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<AppDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());


// ── JWT Auth ──────────────────────────────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                                           Encoding.UTF8.GetBytes(jwtKey)),
        };

        // A password reset must lock out any token issued before it. Since JWTs are
        // stateless, this is enforced by comparing the token's issued-at time against
        // the user's PasswordChangedAt on every authenticated request.
        opt.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var sub = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (sub is null || !Guid.TryParse(sub, out var userId))
                {
                    context.Fail("Invalid token.");
                    return;
                }

                var dbCtx = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var user = await dbCtx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (user is null)
                {
                    context.Fail("User no longer exists.");
                    return;
                }

                if (user.PasswordChangedAt is not null)
                {
                    // The "iat" claim is whole-second precision (JWTs truncate it), but
                    // PasswordChangedAt is sub-second — compare both truncated to seconds,
                    // otherwise a login in the same second as the reset gets rejected.
                    var iat = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Iat);
                    var passwordChangedAtSeconds = new DateTimeOffset(
                        DateTime.SpecifyKind(user.PasswordChangedAt.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    if (iat is null || !long.TryParse(iat, out var iatSeconds) ||
                        iatSeconds < passwordChangedAtSeconds)
                    {
                        context.Fail("Token invalidated by a password reset.");
                    }
                }
            },
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(opt =>
{
    // forgot-password must never surface rate-limit state to the caller, so on
    // rejection we still return the same generic 200 the happy path returns.
    opt.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "If that email is registered, we've sent a reset link." }, token);
    };
    opt.AddPolicy("forgot-password", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
            }));
});

builder.Services.AddSingleton<JwtService>();

// ── Email (Resend) ────────────────────────────────────────────────────────────
builder.Services.AddResend(o =>
{
    o.ApiToken = builder.Configuration["Resend:ApiKey"] ?? "";
    o.ThrowExceptions = false;
});
builder.Services.AddScoped<IEmailService, ResendEmailService>();
builder.Services.AddScoped<PasswordResetService>();
builder.Services.AddControllers()
    .AddJsonOptions(opt =>
    {
        opt.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
        opt.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p => p
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// ── Auto-migrate on startup ───────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await ctx.Database.MigrateAsync();
}

app.Run();