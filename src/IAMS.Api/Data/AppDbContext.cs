using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    private readonly ITenantProvider? _tenantProvider;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        ITenantProvider tenantProvider) : base(options)
    {
        _tenantProvider = tenantProvider;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetAssignment> AssetAssignments => Set<AssetAssignment>();
    public DbSet<WarrantyAlert> WarrantyAlerts => Set<WarrantyAlert>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure Tenant entity
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Slug).IsUnique();

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Slug).HasMaxLength(100).IsRequired();
            entity.Property(e => e.LogoUrl).HasMaxLength(500);
            entity.Property(e => e.PrimaryColor).HasMaxLength(20);
            entity.Property(e => e.SubscriptionTier).HasMaxLength(50).IsRequired();
            entity.Property(e => e.CreatedByUserId).HasMaxLength(450);
        });

        // Configure ApplicationUser with tenant relationship
        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(e => e.FullName).HasMaxLength(100);
            entity.Property(e => e.Department).HasMaxLength(100);

            entity.HasIndex(e => e.TenantId);

            entity.HasOne(e => e.Tenant)
                .WithMany(t => t.Users)
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Configure Asset with tenant
        modelBuilder.Entity<Asset>(entity =>
        {
            entity.HasKey(e => e.Id);

            // AssetTag is unique per tenant (not globally)
            entity.HasIndex(e => new { e.TenantId, e.AssetTag }).IsUnique();
            entity.HasIndex(e => e.TenantId);

            // Primary fields
            entity.Property(e => e.AssetTag).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Manufacturer).HasMaxLength(100);
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.SerialNumber).HasMaxLength(100);
            entity.Property(e => e.DeviceType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.PurchasePrice).HasPrecision(18, 2);
            entity.Property(e => e.Currency).HasMaxLength(3).HasDefaultValue("USD");
            entity.Property(e => e.WarrantyProvider).HasMaxLength(200);
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired();

            // Legacy/additional fields
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Location).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(2000);

            // Ignore computed property
            entity.Ignore(e => e.DisplayName);

            // Tenant relationship
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.AssignedToUser)
                .WithMany(u => u.AssignedAssets)
                .HasForeignKey(e => e.AssignedToUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Global query filter for tenant isolation
            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });

        // Configure AssetAssignment with tenant
        modelBuilder.Entity<AssetAssignment>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.ReturnNotes).HasMaxLength(1000);
            entity.Property(e => e.ReturnCondition).HasMaxLength(50);

            // Ignore computed properties
            entity.Ignore(e => e.IsActive);
            entity.Ignore(e => e.Duration);

            // Index for common queries
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.AssetId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.ReturnedAt });

            // Tenant relationship
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Asset)
                .WithMany()
                .HasForeignKey(e => e.AssetId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.AssignedByUser)
                .WithMany()
                .HasForeignKey(e => e.AssignedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.ReturnedByUser)
                .WithMany()
                .HasForeignKey(e => e.ReturnedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Global query filter
            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });

        // Configure WarrantyAlert with tenant
        modelBuilder.Entity<WarrantyAlert>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.AlertType).HasMaxLength(50).IsRequired();

            // Ignore computed property
            entity.Ignore(e => e.IsAcknowledged);

            // Index for common queries
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.AssetId);
            entity.HasIndex(e => e.AlertType);
            entity.HasIndex(e => e.AcknowledgedAt);
            entity.HasIndex(e => new { e.AssetId, e.AlertType });

            // Tenant relationship
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Asset)
                .WithMany()
                .HasForeignKey(e => e.AssetId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.AcknowledgedByUser)
                .WithMany()
                .HasForeignKey(e => e.AcknowledgedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Global query filter
            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });

        // Configure Attachment with tenant
        modelBuilder.Entity<Attachment>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FileName).HasMaxLength(255).IsRequired();
            entity.Property(e => e.StoredFileName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ContentType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);

            // Index for common queries
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.AssetId);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => new { e.AssetId, e.Category });

            // Tenant relationship
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Asset)
                .WithMany()
                .HasForeignKey(e => e.AssetId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.UploadedByUser)
                .WithMany()
                .HasForeignKey(e => e.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Global query filter
            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });

        // Configure Notification with tenant
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Message).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Type).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Link).HasMaxLength(500);
            entity.Property(e => e.RelatedEntityType).HasMaxLength(50);

            // Index for common queries
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.IsRead);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.UserId, e.IsRead });

            // Tenant relationship
            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Global query filter
            entity.HasQueryFilter(e =>
                _tenantProvider == null ||
                _tenantProvider.IsSuperAdmin() ||
                e.TenantId == _tenantProvider.GetCurrentTenantId());
        });

        // Configure RefreshToken (no tenant filter - tied to user)
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Token).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ReplacedByToken).HasMaxLength(500);
            entity.Property(e => e.CreatedByIp).HasMaxLength(50);
            entity.Property(e => e.RevokedByIp).HasMaxLength(50);

            // Index for fast token lookup
            entity.HasIndex(e => e.Token);
            entity.HasIndex(e => e.UserId);

            // Ignore computed properties
            entity.Ignore(e => e.IsExpired);
            entity.Ignore(e => e.IsRevoked);
            entity.Ignore(e => e.IsActive);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override int SaveChanges()
    {
        SetTenantIdOnNewEntities();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SetTenantIdOnNewEntities();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void SetTenantIdOnNewEntities()
    {
        var tenantId = _tenantProvider?.GetCurrentTenantId();
        if (!tenantId.HasValue) return;

        foreach (var entry in ChangeTracker.Entries<ITenantEntity>()
            .Where(e => e.State == EntityState.Added && e.Entity.TenantId == Guid.Empty))
        {
            entry.Entity.TenantId = tenantId.Value;
        }

        // Also set TenantId for ApplicationUser (not ITenantEntity)
        foreach (var entry in ChangeTracker.Entries<ApplicationUser>()
            .Where(e => e.State == EntityState.Added && e.Entity.TenantId == Guid.Empty))
        {
            entry.Entity.TenantId = tenantId.Value;
        }
    }
}
