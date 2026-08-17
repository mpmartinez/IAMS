using System.Text;
using IAMS.Api.Authorization;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using QuestPDF.Infrastructure;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not set. Supply the PostgreSQL connection string via the " +
        "ConnectionStrings__DefaultConnection environment variable (or user-secrets in development).");
}

builder.Services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(connectionString, npgsql =>
    {
        // A managed or containerised database can drop connections without warning - a
        // restart, a failover, an idle timeout, a network blip. Retry transient failures
        // instead of surfacing them as 500s. Note this also forces any user-initiated
        // transaction to run through Database.CreateExecutionStrategy() - see
        // TicketService.Fulfilment.cs, which is the only place that opens one.
        npgsql.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null);
    });
    // AuditSaveChangesInterceptor holds mutable per-operation state (_pending, _writing).
    // That is safe only because AddDbContext's optionsLifetime defaults to Scoped, so this
    // lambda — and this `new` — runs once per request scope. Switching to AddDbContextPool,
    // or passing optionsLifetime: ServiceLifetime.Singleton, would share one instance across
    // concurrent requests and let one tenant's pending audit rows flush through another
    // tenant's context. Do not change the lifetime without making this interceptor stateless.
    options.AddInterceptors(
        new AuditSaveChangesInterceptor(
            serviceProvider.GetRequiredService<ICurrentUserAccessor>(),
            serviceProvider.GetRequiredService<ILogger<AuditSaveChangesInterceptor>>()));
});

// Identity
builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength = 8;
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedEmail = false;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// JWT signing key. Deliberately has no in-source fallback: a default key committed to the
// repo is a public key, and anything it signs can be forged by anyone who reads the repo.
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key is missing or shorter than 32 bytes. Set it via the Jwt__Key environment variable.");
}

// Authentication - Local JWT
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
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };

    // Support token from query string for SSE
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/api/notifications/stream"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

// Authorization policies.
//
// Every policy below resolves to a permission the tenant can retune at /admin/roles. The two
// SuperAdmin policies stay role-based: they guard platform-level endpoints (tenants, shared
// reference data) that no tenant may grant itself.
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddAuthorizationBuilder()
    .RequirePermission("CanCreateAssets", Permissions.AssetsCreate)
    .RequirePermission("CanEditAssets", Permissions.AssetsEdit)
    .RequirePermission("CanDeleteAssets", Permissions.AssetsDelete)
    .RequirePermission("CanManageAssets", Permissions.AssetsEdit)
    .RequirePermission("CanImportAssets", Permissions.AssetsImport)
    .RequirePermission("CanViewAssets", Permissions.AssetsView)
    .RequirePermission("Admin", Permissions.AssetsDebug)
    .RequirePermission("CanViewReports", Permissions.ReportsView)
    .RequirePermission("CanAssignAssets", Permissions.AssignmentsAssign)
    .RequirePermission("CanReturnAssets", Permissions.AssignmentsReturn)
    .RequirePermission("CanViewAssignments", Permissions.AssignmentsView)
    .RequirePermission("CanFileTickets", Permissions.TicketsFile)
    .RequirePermission("CanViewTicketQueue", Permissions.TicketsQueue)
    .RequirePermission("CanManageTicketQueue", Permissions.TicketsManage)
    .RequirePermission("CanViewUsers", Permissions.UsersView)
    .RequirePermission("CanManageUsers", Permissions.UsersManage)
    .RequirePermission("CanViewUsersList", Permissions.UsersRead)
    .RequirePermission("CanViewRoles", Permissions.RolesView)
    .RequirePermission("CanManageRoles", Permissions.RolesManage)
    .RequirePermission("CanManageAttachments", Permissions.AttachmentsManage)
    .RequirePermission("CanManageWarrantyAlerts", Permissions.WarrantyManage)
    .RequirePermission("CanDeleteWarrantyAlerts", Permissions.WarrantyDelete)
    .RequirePermission("CanSendTestNotifications", Permissions.NotificationsTest)
    // Platform-level: not tenant-tunable.
    .AddPolicy("SuperAdmin", policy => policy.RequireRole(Roles.SuperAdmin))
    .AddPolicy("CanManageTenants", policy => policy.RequireRole(Roles.SuperAdmin));

// Services
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, TenantProvider>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddSingleton<IQrCodeService, QrCodeService>();
builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<IAssetImportService, AssetImportService>();
builder.Services.AddSingleton<IPdfReportService, PdfReportService>();
builder.Services.AddSingleton<INotificationService, NotificationService>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
builder.Services.AddScoped<ITicketNumberAllocator, TicketNumberAllocator>();
builder.Services.AddScoped<ITicketService, TicketService>();
builder.Services.AddScoped<ILookupService, LookupService>();
builder.Services.AddScoped<IPermissionResolver, PermissionResolver>();

// Background Services
builder.Services.AddHostedService<WarrantyCheckService>();

// Health Checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

// Configure form options for file uploads (needed for larger files on mobile)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 10 * 1024 * 1024; // 10 MB for multipart body
    options.ValueLengthLimit = 10 * 1024 * 1024; // 10 MB for individual values
});

// Controllers
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "IAMS API", Version = "v1" });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header. Enter 'Bearer' [space] and your token.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
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

// CORS
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["https://localhost:5022", "http://localhost:5022"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazor", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()
            .WithExposedHeaders("Content-Disposition");
    });
});

var app = builder.Build();

// Migrate database and seed data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

    // With more than one replica booting together, two instances can both see "not migrated /
    // not seeded" and race: EnsureRolePermissionsAsync is check-then-act on
    // Tenant.RolePermissionsSeededAt, so the loser's insert trips the unique index and
    // DbUpdateException escapes SeedData.Initialize, and that instance never reaches app.Run() -
    // a crash-loop. A Postgres session-level advisory lock around the whole migrate+seed block
    // serializes replicas: the second one blocks here until the first finishes and commits, then
    // finds the marker already set and does nothing.
    //
    // Deliberately held here, not inside SeedData itself - SeedData.EnsureRolePermissionsAsync is
    // also called directly by the test suite (SQLite, no advisory locks) and by
    // TenantsController when a single tenant is created at runtime, neither of which need or can
    // use this.
    const long MigrateAndSeedLockKey = 851917;

    await db.Database.OpenConnectionAsync();
    try
    {
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({MigrateAndSeedLockKey})");
        try
        {
            await db.Database.MigrateAsync();
            await SeedData.Initialize(db, userManager, roleManager);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({MigrateAndSeedLockKey})");
        }
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowBlazor");
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
