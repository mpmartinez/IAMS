using System.Text;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
        "ConnectionStrings:DefaultConnection is not set. Supply the Neon connection string via the " +
        "ConnectionStrings__DefaultConnection environment variable (or user-secrets in development).");
}

builder.Services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(connectionString, npgsql =>
    {
        // Neon suspends idle computes, so the first connection after a pause can time out
        // or drop. Retry transient failures instead of surfacing them as 500s.
        npgsql.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null);
    });
    options.AddInterceptors(
        new AuditSaveChangesInterceptor(
            serviceProvider.GetRequiredService<ICurrentUserAccessor>(),
            serviceProvider.GetRequiredService<ILogger<AuditSaveChangesInterceptor>>()));
});

// Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
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

// Authorization policies
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", policy => policy.RequireRole("Admin"))
    .AddPolicy("Staff", policy => policy.RequireRole("Admin", "Staff"))
    .AddPolicy("Auditor", policy => policy.RequireRole("Admin", "Auditor"))
    .AddPolicy("CanManageAssets", policy => policy.RequireRole("Admin", "Staff"))
    .AddPolicy("CanViewReports", policy => policy.RequireRole("Admin", "Auditor"))
    .AddPolicy("CanViewUsersList", policy => policy.RequireRole("Admin", "Staff"))
    .AddPolicy("CanCreateAssets", policy => policy.RequireRole("Admin", "Staff"))
    .AddPolicy("CanEditAssets", policy => policy.RequireRole("Admin", "Staff"))
    .AddPolicy("CanDeleteAssets", policy => policy.RequireRole("Admin"))
    .AddPolicy("CanAssignAssets", policy => policy.RequireRole("Admin", "Staff"))
    .AddPolicy("CanReturnAssets", policy => policy.RequireRole("Admin", "Staff"))
    .AddPolicy("CanViewAssignments", policy => policy.RequireRole("Admin", "Staff", "Auditor"))
    .AddPolicy("CanManageMaintenance", policy => policy.RequireRole("Admin", "Staff"))
    // Multi-tenant policies
    .AddPolicy("SuperAdmin", policy => policy.RequireRole("SuperAdmin"))
    .AddPolicy("TenantAdmin", policy => policy.RequireAssertion(context =>
        context.User.IsInRole("SuperAdmin") ||
        context.User.HasClaim(c => c.Type == "is_tenant_admin" && c.Value == "true")))
    .AddPolicy("CanManageTenants", policy => policy.RequireRole("SuperAdmin"))
    .AddPolicy("CanManageOrgSettings", policy => policy.RequireAssertion(context =>
        context.User.IsInRole("SuperAdmin") ||
        context.User.HasClaim(c => c.Type == "is_tenant_admin" && c.Value == "true")));

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
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    db.Database.Migrate();
    await SeedData.Initialize(db, userManager, roleManager);
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
