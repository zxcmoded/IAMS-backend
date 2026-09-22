using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using IAMS.Api.Features.Admin.ResetUserActivation;
using IAMS.Api.Features.Auth.Activate;
using IAMS.Api.Features.Auth.Logout;
using IAMS.Api.Features.MasterData.ListBins;
using IAMS.Api.Features.MasterData.ListCompanies;
using IAMS.Api.Features.MasterData.ListLocations;
using IAMS.Api.Features.MasterData.ListRacks;
using IAMS.Api.Features.MasterData.ListWarehouses;
using IAMS.Api.Features.Users.AssignUserLocations;
using IAMS.Api.Features.Users.GetUserLocations;
using IAMS.Api.Features.Inventory.AdjustStock;
using IAMS.Api.Features.Inventory.ApproveStockCount;
using IAMS.Api.Features.Inventory.CreateStockCount;
using IAMS.Api.Features.Inventory.GetInventoryItem;
using IAMS.Api.Features.Inventory.ListInventory;
using IAMS.Api.Features.Inventory.ReceiveStock;
using IAMS.Api.Features.Inventory.RejectStockCount;
using IAMS.Api.Features.Inventory.SyncItems;
using IAMS.Api.Features.Inventory.SyncStockLevels;
using IAMS.Api.Features.Inventory.TransferStock;
using IAMS.Api.Features.Scanning.ResolveScan;
using IAMS.Api.Features.Scope.GetEffectiveScope;
using IAMS.Api.Common.Inventory;
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

        // Normalize minimal-API parameter-binding failures (bad parentId/pageSize) to the standard
        // ProblemDetails+code shape instead of a raw framework 400. See BadRequestExceptionHandler.
        services.AddExceptionHandler<Errors.BadRequestExceptionHandler>();

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
        services.AddScoped<AccessScopeResolver>();

        // Feature handlers.
        services.AddScoped<ActivateHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<GetEffectiveScopeHandler>();
        services.AddScoped<ResetUserActivationHandler>();
        services.AddScoped<AssignUserLocationsHandler>();
        services.AddScoped<GetUserLocationsHandler>();
        services.AddScoped<ListCompaniesHandler>();
        services.AddScoped<ListLocationsHandler>();
        services.AddScoped<ListWarehousesHandler>();
        services.AddScoped<ListRacksHandler>();
        services.AddScoped<ListBinsHandler>();

        // Phase 2a — F3 Scanning + F4 Inventory Operations.
        services.AddScoped<StockMovementService>();
        services.AddScoped<ResolveScanHandler>();
        services.AddScoped<ListInventoryHandler>();
        services.AddScoped<GetInventoryItemHandler>();
        services.AddScoped<ReceiveStockHandler>();
        services.AddScoped<TransferStockHandler>();
        services.AddScoped<AdjustStockHandler>();
        services.AddScoped<CreateStockCountHandler>();
        services.AddScoped<ApproveStockCountHandler>();
        services.AddScoped<RejectStockCountHandler>();
        services.AddScoped<SyncItemsHandler>();
        services.AddScoped<SyncStockLevelsHandler>();

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        // Role-based authorization policies (minimum-role gates keyed off the JWT `role` int claim). See
        // Policies for the mapping (read / write / manage_inventory / manage_company). Per-request
        // Company/Location scoping is enforced separately in handlers via AccessScopeResolver.
        services.AddAuthorization(options => options.AddIamsAuthorization());
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
        app.MapActivateEndpoint();
        app.MapLogoutEndpoint();
        app.MapGetEffectiveScopeEndpoint();
        app.MapResetUserActivationEndpoint();
        app.MapAssignUserLocationsEndpoint();
        app.MapGetUserLocationsEndpoint();
        app.MapListCompaniesEndpoint();
        app.MapListLocationsEndpoint();
        app.MapListWarehousesEndpoint();
        app.MapListRacksEndpoint();
        app.MapListBinsEndpoint();

        // Phase 2a — F3 Scanning + F4 Inventory Operations.
        app.MapResolveScanEndpoint();
        app.MapListInventoryEndpoint();
        app.MapGetInventoryItemEndpoint();
        app.MapReceiveStockEndpoint();
        app.MapTransferStockEndpoint();
        app.MapAdjustStockEndpoint();
        app.MapCreateStockCountEndpoint();
        app.MapApproveStockCountEndpoint();
        app.MapRejectStockCountEndpoint();
        app.MapSyncItemsEndpoint();
        app.MapSyncStockLevelsEndpoint();
        return app;
    }
}
