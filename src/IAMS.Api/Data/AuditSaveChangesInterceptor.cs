using System.Text.Json;
using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

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
///
/// Trade-off, accepted deliberately: audit rows commit in a second transaction, issued
/// after the business transaction has already committed. If that second save fails, the
/// business change stands but its audit trail does not — the failure is logged at error
/// level and swallowed rather than rethrown, so a lost audit row never turns into a
/// duplicate ticket or asset from a client retrying an operation that actually succeeded.
/// Making both writes commit atomically would mean this interceptor manages a transaction
/// spanning both saves; that is a real gap, left as a deliberate open follow-up rather than
/// solved here.
/// </summary>
public class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<string> AuditedTypes =
        [nameof(Asset), nameof(AssetAssignment), nameof(Ticket), nameof(TicketComment), nameof(TicketAttachment)];

    // Noise, or already captured by the audited fields themselves.
    private static readonly HashSet<string> IgnoredProperties =
        ["CreatedAt", "UpdatedAt"];

    /// <summary>Individual string values in Changes are capped so a long comment edit doesn't
    /// write its full before-and-after text into a table that only ever grows.</summary>
    private const int MaxSerialisedValueLength = 500;

    private readonly ICurrentUserAccessor _currentUser;
    private readonly ILogger<AuditSaveChangesInterceptor> _logger;

    /// <summary>Audit rows built during SavingChanges, persisted during SavedChanges.</summary>
    private readonly List<PendingAudit> _pending = [];

    /// <summary>True while writing audit rows, so the second save does not re-enter collection.</summary>
    private bool _writing;

    private sealed record PendingAudit(AuditLog Log, EntityEntry? InsertedEntry);

    public AuditSaveChangesInterceptor(ICurrentUserAccessor currentUser, ILogger<AuditSaveChangesInterceptor> logger)
    {
        _currentUser = currentUser;
        _logger = logger;
    }

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
        await FlushAsync(eventData.Context);
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
        try
        {
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            DetachAndLogFailure(context, logs, ex);
        }
        finally
        {
            _writing = false;
        }
    }

    private async Task FlushAsync(DbContext? context)
    {
        if (context is null) return;
        var logs = TakeResolved();
        if (logs is null) return;

        context.AddRange(logs);
        _writing = true;
        try
        {
            // By this point the business transaction has already committed, so the caller's
            // token (often HttpContext.RequestAborted) is irrelevant to whether the audit
            // write should proceed: a client disconnecting must not cancel the audit save,
            // or the evidence for an already-committed change silently disappears.
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            DetachAndLogFailure(context, logs, ex);
        }
        finally
        {
            _writing = false;
        }
    }

    /// <summary>
    /// A failed audit save must not fail the caller's already-committed operation, and the
    /// AuditLog entities EF still has tracked as Added must not leak into some unrelated
    /// later SaveChanges on the same context. Detach them, log the loss, and swallow it.
    /// </summary>
    private void DetachAndLogFailure(DbContext context, List<AuditLog> logs, Exception ex)
    {
        foreach (var log in logs)
            context.Entry(log).State = EntityState.Detached;

        var entityTypes = string.Join(", ", logs.Select(l => l.EntityType).Distinct());
        _logger.LogError(ex,
            "Failed to persist {Count} audit log entries for entity types [{EntityTypes}]. " +
            "The underlying business change already committed; this audit evidence is lost.",
            logs.Count, entityTypes);
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
                from = CapValue(property.OriginalValue),
                to = CapValue(property.CurrentValue)
            };
        }

        return changes.Count == 0 ? null : JsonSerializer.Serialize(changes);
    }

    /// <summary>Long string values (e.g. a ticket comment body) are truncated before being
    /// written to Changes, an append-only table. Non-string values are left alone.</summary>
    private static object? CapValue(object? value)
    {
        if (value is string s && s.Length > MaxSerialisedValueLength)
            return $"{s[..MaxSerialisedValueLength]}… [truncated, {s.Length} chars]";

        return value;
    }
}
