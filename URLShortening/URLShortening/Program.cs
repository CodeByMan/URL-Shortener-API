using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using URLShortening.Data;
using URLShortening.Data.Repository;
using URLShortening.Helpers;
using URLShortening.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdentity<User, IdentityRole>(
        config =>
        {
            config.Tokens.AuthenticatorTokenProvider
                = TokenOptions.DefaultAuthenticatorProvider;
            config.SignIn.RequireConfirmedEmail = true;
        })
    .AddDefaultTokenProviders()
    .AddEntityFrameworkStores<DataContext>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(op =>
    {
        op.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAudience = builder.Configuration["JWT:Audience"],
            ValidIssuer = builder.Configuration["JWT:Issuer"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["JWT:Key"] ??
                                       throw new InvalidOperationException(
                                           "JWT:Key is not configured.")))
        };
        op.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userId = context.Principal?
                    .FindFirstValue(ClaimTypes.NameIdentifier);
                var tokenSecurityStamp = context.Principal?
                    .FindFirstValue("security_stamp");

                if (string.IsNullOrWhiteSpace(userId) ||
                    string.IsNullOrWhiteSpace(tokenSecurityStamp))
                {
                    context.Fail("The token is missing required claims.");
                    return;
                }

                var userManager = context.HttpContext.RequestServices
                    .GetRequiredService<UserManager<User>>();
                var user = await userManager.FindByIdAsync(userId);

                if (user is null ||
                    !string.Equals(user.SecurityStamp,
                        tokenSecurityStamp,
                        StringComparison.Ordinal))
                {
                    context.Fail("The token has been revoked.");
                }
            }
        };
    });

builder.Services.AddAutoMapper(AppDomain.CurrentDomain.GetAssemblies());

var connectionString
    = builder.Configuration.GetConnectionString("DefaultConnection") ??
      throw new InvalidOperationException(
          "ConnectionStrings:DefaultConnection is not configured.");

builder.Services.AddDbContext<DataContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                               ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    var knownProxies = builder.Configuration
        .GetSection("ForwardedHeaders:KnownProxies")
        .Get<string[]>() ?? [];

    foreach (var proxy in knownProxies)
    {
        if (IPAddress.TryParse(proxy, out var address))
        {
            options.KnownProxies.Add(address);
        }
    }
});

var authenticationLimit = Math.Clamp(
    builder.Configuration.GetValue<int?>(
        "RateLimiting:AuthenticationPermitLimit") ?? 10,
    1,
    100);
var anonymousCreationLimit = Math.Clamp(
    builder.Configuration.GetValue<int?>(
        "RateLimiting:AnonymousCreationPermitLimit") ?? 10,
    1,
    100);
var authenticatedCreationLimit = Math.Clamp(
    builder.Configuration.GetValue<int?>(
        "RateLimiting:AuthenticatedCreationPermitLimit") ?? 60,
    1,
    500);
var publicRedirectLimit = Math.Clamp(
    builder.Configuration.GetValue<int?>(
        "RateLimiting:PublicRedirectPermitLimit") ?? 240,
    10,
    5000);
var analyticsLimit = Math.Clamp(
    builder.Configuration.GetValue<int?>(
        "RateLimiting:AnalyticsPermitLimit") ?? 30,
    1,
    500);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicyNames.Authentication, context =>
        CreateFixedWindowPartition(context,
            authenticationLimit,
            "authentication"));

    options.AddPolicy(RateLimitPolicyNames.UrlCreation, context =>
    {
        var permitLimit = context.User.Identity?.IsAuthenticated == true
            ? authenticatedCreationLimit
            : anonymousCreationLimit;
        return CreateFixedWindowPartition(context, permitLimit,
            "url-creation");
    });

    options.AddPolicy(RateLimitPolicyNames.PublicRedirect, context =>
        CreateFixedWindowPartition(context,
            publicRedirectLimit,
            "public-redirect"));

    options.AddPolicy(RateLimitPolicyNames.Analytics, context =>
        CreateFixedWindowPartition(context,
            analyticsLimit,
            "analytics"));

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests.",
            Detail = "The request rate limit was exceeded. Try again later."
        }, cancellationToken: cancellationToken);
    };
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1",
        new OpenApiInfo
        {
            Title = "URL Shortening & Analytics API",
            Version = "v1",
            Description = "Developed by Muhammad Ali Nawaz"
        });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
});

builder.Services.AddVersionedApiExplorer(options =>
{
    options.GroupNameFormat = "'v'VVV";
    options.SubstituteApiVersionInUrl = true;
});

builder.Services.AddScoped<IUserHelper, UserHelper>();
builder.Services.AddScoped<IMailService, MailService>();
builder.Services.AddScoped<ICodeGeneratorHelper, CodeGeneratorHelper>();
builder.Services.AddScoped<IUrlRepository, UrlRepository>();
builder.Services.AddScoped<IAccessLogRepository, AccessLogRepository>();
builder.Services.AddSingleton<IDeviceInfoHelper, DeviceInfoHelper>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IGeoHelper, GeoHelper>(client =>
{
    var baseUrl = builder.Configuration["GeoLocation:BaseUrl"] ??
                  "https://ipwho.is/";
    client.BaseAddress = new Uri(baseUrl);

    var timeoutSeconds = builder.Configuration
        .GetValue<int?>("GeoLocation:TimeoutSeconds") ?? 3;
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 30));
});

var app = builder.Build();

app.UseForwardedHeaders();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features
            .Get<IExceptionHandlerFeature>()?.Error;
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("GlobalExceptionHandler");

        logger.LogError(exception,
            "An unhandled exception occurred while processing {Method} {Path}.",
            context.Request.Method,
            context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "An unexpected server error occurred.",
                detail: "The request could not be completed.")
            .ExecuteAsync(context);
    });
});

app.UseSwagger();
app.UseSwaggerUI();

app.UseStaticFiles();
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/", () => Results.Json(new
{
    Message = "URL Shortening & Analytics API is running.",
    Documentation = "/swagger/index.html",
    Developer = "Muhammad Ali Nawaz",
    Timestamp = DateTime.UtcNow
}));

app.MapFallback(() => Results.NotFound(new
{
    Message = "The requested resource was not found.",
    Timestamp = DateTime.UtcNow
}));

await app.RunAsync();

static RateLimitPartition<string> CreateFixedWindowPartition(
    HttpContext context,
    int permitLimit,
    string policyName)
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    var clientKey = !string.IsNullOrWhiteSpace(userId)
        ? $"user:{userId}"
        : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    return RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: $"{policyName}:{clientKey}",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
}

public partial class Program
{
}
