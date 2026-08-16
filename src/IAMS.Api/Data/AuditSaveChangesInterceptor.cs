using System.Text.Json;
using IAMS.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IAMS.Api.Data;

public interface ICurrentUserAccessor
{
    string? GetUserId();
}

public class HttpContextCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentUserAccessor(IHttpContextAccessor accessor) => _accessor = accessor;

    public string? GetUserId() =>
        _accessor.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
}

/// <summary>
/// Writes an append-only AuditLog row for every insert, update and delete of an audited
/// entity. Lives at the DbContext layer so no controller can forget to record history.
/// </summary>
public class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<string> AuditedTypes =
        [nameof(Asset), nameof(AssetAssignment), nameof(Ticket), nameof(TicketComment), nameof(TicketAttachment)];

    // Noise, or already captured by the audited fields themselves.
    private static readonly HashSet<string> IgnoredProperties =
        ["CreatedAt", "UpdatedAt"];

    private readonly ICurrentUserAccessor _currentUser;

    /// <summary>Audit rows built during SavingChanges, persisted during SavedChanges.</summary>
    private readonly List<PendingAudit> _pending = [];

    /// <summary>True while writing audit rows, so the second save does not re-enter collection.</summary>
    private bool _writing;

    private sealed record PendingAudit(AuditLog Log, EntityEntry? InsertedEntry);

    public AuditSaveChangesInterceptor(ICurrentUserAccessor currentUser) => _currentUser = currentUser;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await FlushAsync(eventData.Context, cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    private void Collect(DbContext? context)
    {
        if (context is null || _writing) return;

        _pending.Clear();
        var userId = _currentUser.GetUserId();

        var tracked = context.ChangeTracker.Entries()
            .Where(e => AuditedTypes.Contains(e.Metadata.ClrType.Name))
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in tracked)
        {
            var tenantId = ReadTenantId(entry);
            if (tenantId is null) continue;

            var isInsert = entry.State == EntityState.Added;

            var log = new AuditLog
            {
                TenantId = tenantId.Value,
                EntityType = entry.Metadata.ClrType.Name,
                // An inserted row has no identity value yet; resolved in Flush.
                EntityId = isInsert ? "" : ReadKey(entry),
                UserId = userId,
                Timestamp = DateTime.UtcNow,
                Action = entry.State switch
                {
                    EntityState.Added => AuditActions.Created,
                    EntityState.Modified => AuditActions.Updated,
                    _ => AuditActions.Deleted
                },
                Changes = entry.State == EntityState.Modified ? SerialiseChanges(entry) : null
            };

            _pending.Add(new PendingAudit(log, isInsert ? entry : null));
        }
    }

    private List<AuditLog>? TakeResolved()
    {
        if (_pending.Count == 0 || _writing) return null;

        var batch = _pending.ToList();
        _pending.Clear();

        foreach (var item in batch)
        {
            if (item.InsertedEntry is not null)
                item.Log.EntityId = ReadKey(item.InsertedEntry);
        }

        return batch.Select(b => b.Log).ToList();
    }

    private void Flush(DbContext? context)
    {
        if (context is null) return;
        var logs = TakeResolved();
        if (logs is null) return;

        context.AddRange(logs);
        _writing = true;
        try { context.SaveChanges(); }
        finally { _writing = false; }
    }

    private async Task FlushAsync(DbContext? context, CancellationToken ct)
    {
        if (context is null) return;
        var logs = TakeResolved();
        if (logs is null) return;

        context.AddRange(logs);
        _writing = true;
        try { await context.SaveChangesAsync(ct); }
        finally { _writing = false; }
    }

    private static Guid? ReadTenantId(EntityEntry entry)
    {
        var property = entry.Properties.FirstOrDefault(p => p.Metadata.Name == nameof(ITenantEntity.TenantId));
        return property?.CurrentValue as Guid?;
    }

    private static string ReadKey(EntityEntry entry)
    {
        // For an inserted row the store-generated key is not known yet; EF fills it in
        // after the insert, so read CurrentValue lazily via the tracked property.
        var key = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey());
        return key?.CurrentValue?.ToString() ?? "";
    }

    private static string? SerialiseChanges(EntityEntry entry)
    {
        var changes = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            if (!property.IsModified) continue;
            if (IgnoredProperties.Contains(property.Metadata.Name)) continue;
            if (Equals(property.OriginalValue, property.CurrentValue)) continue;

            changes[property.Metadata.Name] = new
            {
                from = property.OriginalValue,
                to = property.CurrentValue
            };
        }

        return changes.Count == 0 ? null : JsonSerializer.Serialize(changes);
    }
}
