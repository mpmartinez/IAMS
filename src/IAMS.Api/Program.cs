using System.Text;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

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
    // Match the role claim type used by m2ID JWT tokens
    options.ClaimsIdentity.RoleClaimType = System.Security.Claims.ClaimTypes.Role;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// Authentication - Support both m2ID OIDC and local JWT
var m2idAuthority = builder.Configuration["m2ID:Authority"];
var useM2ID = !string.IsNullOrEmpty(m2idAuthority);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    if (useM2ID)
    {
        // Use m2ID as identity provider via OIDC discovery
        options.Authority = m2idAuthority;
        options.RequireHttpsMetadata = builder.Configuration.GetValue<bool>("m2ID:RequireHttpsMetadata", false);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = false, // m2ID tokens may have different audience
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "name",
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
    }
    else
    {
        // Fallback to local JWT validation
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    }

    // Support token from query string for SSE (EventSource doesn't support headers)
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            logger.LogInformation("=== OnMessageReceived: Auth header = {AuthHeader}", authHeader ?? "(none)");

            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            // If the request is for SSE notifications stream, accept token from query string
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/api/notifications/stream"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        },
        OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogError(context.Exception, "=== JWT Authentication Failed ===");
            return Task.CompletedTask;
        },
        OnTokenValidated = async context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            var request = context.HttpContext.Request;
            var isStaff = context.Principal?.IsInRole("Staff") ?? false;
            var isAdmin = context.Principal?.IsInRole("Admin") ?? false;
            logger.LogWarning("=== {Method} {Path} | Staff={Staff} Admin={Admin}",
                request.Method, request.Path, isStaff, isAdmin);

            // Sync m2ID user to local database on first authenticated request
            if (context.Principal is not null)
            {
                try
                {
                    var userSyncService = context.HttpContext.RequestServices.GetRequiredService<UserSyncService>();
                    await userSyncService.SyncUserFromClaimsAsync(context.Principal);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to sync m2ID user to local database");
                }
            }
        }
    };
});

// Authorization policies - Permission-based (m2ID centralized RBAC)
builder.Services.AddAuthorizationBuilder()
    // Fallback role-based policies (for backwards compatibility)
    .AddPolicy("Admin", policy => policy.RequireRole("Admin", "Administrator"))
    .AddPolicy("Staff", policy => policy.RequireRole("Admin", "Administrator", "Staff"))
    .AddPolicy("Auditor", policy => policy.RequireRole("Admin", "Administrator", "Auditor"))
    // Permission-based policies (from m2ID JWT token)
    .AddPolicy("CanCreateAssets", policy => policy.RequireAssertion(ctx =>
        ctx.User.HasClaim("permission", "iams:assets:create") ||
        ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Administrator")))
    .AddPolicy("CanReadAssets", policy => policy.RequireAssertion(ctx =>
        ctx.User.HasClaim("permission", "iams:assets:read") ||
        ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Administrator") ||
        ctx.User.IsInRole("Staff") || ctx.User.IsInRole("Auditor")))
    .AddPolicy("CanEditAssets", policy => policy.RequireAssertion(ctx =>
        ctx.User.HasClaim("permission", "iams:assets:edit") ||
        ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Administrator")))
    .AddPolicy("CanDeleteAssets", policy => policy.RequireAssertion(ctx =>
        ctx.User.HasClaim("permission", "iams:assets:delete") ||
        ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Administrator")))
    .AddPolicy("CanAssignAssets", policy => policy.RequireAssertion(ctx =>
        ctx.User.HasClaim("permission", "iams:assignments:create") ||
        ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Administrator")))
    .AddPolicy("CanViewAssignments", policy => policy.RequireAssertion(ctx =>
        ctx.User.HasClaim("permission", "iams:assignments:read") ||
        ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Administrator") ||
        ctx.User.IsInRole("Staff")))
    .AddPolicy("CanReturnAssets", policy => policy.RequireAssertion(ctx =>
        ctx.User.HasClaim("permission", "iams:assignments:return") ||
        ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Administrator")))
    .AddPolicy("CanViewReports", policy => policy.RequireAssertion(ctx =>
        ctx.User.HasClaim("permission", "iams:reports:view") ||
        ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Administrator") ||
        ctx.User.IsInRole("Auditor")))
    .AddPolicy("CanViewUsersList", policy => policy.RequireAssertion(ctx =>
        ctx.User.HasClaim("permission", "iams:users:read") ||
        ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Administrator") ||
        ctx.User.IsInRole("Staff")));

// Services
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<UserSyncService>();
builder.Services.AddSingleton<IQrCodeService, QrCodeService>();
builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddSingleton<INotificationService, NotificationService>();

// Background Services
builder.Services.AddHostedService<WarrantyCheckService>();

// Health Checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

// Controllers
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "IAMS API", Version = "v1" });

    // Add JWT Authentication to Swagger
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token.",
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
    ?? ["https://localhost:5022", "http://localhost:5003"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazor", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
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
else
{
    app.UseHttpsRedirection();
}

app.UseCors("AllowBlazor");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
