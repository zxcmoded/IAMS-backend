using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Auth;
using IAMS.Api.Common.Connections;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using IAMS.Api.Features.Access.EvaluateAccess;
using IAMS.Api.Features.Access.EvaluateAccessBatch;
using IAMS.Api.Features.Admin.ResetUserDeviceBinding;
using IAMS.Api.Features.Auth.Login;
using IAMS.Api.Features.Auth.Logout;
using IAMS.Api.Features.Auth.RefreshToken;
using IAMS.Api.Features.Auth.ResendTwoFactor;
using IAMS.Api.Features.Auth.VerifyTwoFactor;
using IAMS.Api.Features.Scope.GetEffectiveScope;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace IAMS.Api.Common;

public static class DependencyInjection
{
    /// <summary>Rate-limit policy name applied to the unauthenticated auth endpoints.</summary>
    public const string AuthRateLimitPolicy = "auth";

    public static IServiceCollection AddIamsServices(
        this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        // Serialize enums as strings so the mobile contract uses "Read"/"Full", "ParentToChild", etc.
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddProblemDetails();

        // Throttle unauthenticated auth endpoints per client IP to close the unlimited online
        // brute-force gap (distinct from the deliberately-deferred account-lockout policy).
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });

        services.AddDbContext<IamsDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Default")));

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));

        // Resolve the signing key ONCE, failing fast outside Development if it's missing/too short. The
        // resolved key is written back into JwtOptions so the signer (JwtTokenService) and the validator
        // below always use the same key — no fail-open on a well-known placeholder in prod.
        var configuredKey = config.GetSection(JwtOptions.SectionName)[nameof(JwtOptions.SigningKey)];
        var signingKey = ResolveSigningKey(configuredKey, env);
        services.PostConfigure<JwtOptions>(o => o.SigningKey = signingKey);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddSingleton<IClock, SystemClock>();

        // Shared infrastructure services.
        services.AddSingleton<JwtTokenService>();
        services.AddScoped<SessionIssuer>();
        services.AddScoped<ActiveScopeResolver>();
        services.AddScoped<AccessCheckService>();
        services.AddScoped<ConnectionPolicyService>();

        // Feature handlers.
        services.AddScoped<LoginHandler>();
        services.AddScoped<VerifyTwoFactorHandler>();
        services.AddScoped<ResendTwoFactorHandler>();
        services.AddScoped<RefreshTokenHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<GetEffectiveScopeHandler>();
        services.AddScoped<EvaluateAccessHandler>();
        services.AddScoped<EvaluateAccessBatchHandler>();
        services.AddScoped<ResetUserDeviceBindingHandler>();

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        // Platform-wide admin gate (Users.IsSystemAdmin), NOT scoped to any tenant/company. Distinct from
        // per-company roles (UserCompanyMembership.RoleId), which are not enforced yet.
        services.AddAuthorization(options =>
        {
            options.AddPolicy("SystemAdmin", policy =>
                policy.RequireClaim(IamsClaims.IsSystemAdmin, "true"));
        });
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var jwt = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        return services;
    }

    /// <summary>
    /// Returns a valid signing key or throws. A missing/short key is only tolerated in Development (with a
    /// clearly-insecure dev key); in every other environment a misconfigured secret is a startup failure,
    /// never a silent fall-back to a well-known key that would let forged tokens validate.
    /// </summary>
    private static string ResolveSigningKey(string? configured, IHostEnvironment env)
    {
        const int minBytes = 32;
        if (!string.IsNullOrEmpty(configured) && Encoding.UTF8.GetByteCount(configured) >= minBytes)
        {
            return configured;
        }

        if (env.IsDevelopment())
        {
            return "iams-development-only-insecure-signing-key";
        }

        throw new InvalidOperationException(
            $"Configuration '{JwtOptions.SectionName}:{nameof(JwtOptions.SigningKey)}' must be set and at " +
            $"least {minBytes} bytes outside the Development environment.");
    }

    public static IEndpointRouteBuilder MapIamsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapLoginEndpoint();
        app.MapVerifyTwoFactorEndpoint();
        app.MapResendTwoFactorEndpoint();
        app.MapRefreshTokenEndpoint();
        app.MapLogoutEndpoint();
        app.MapGetEffectiveScopeEndpoint();
        app.MapEvaluateAccessEndpoint();
        app.MapEvaluateAccessBatchEndpoint();
        app.MapResetUserDeviceBindingEndpoint();
        return app;
    }
}
