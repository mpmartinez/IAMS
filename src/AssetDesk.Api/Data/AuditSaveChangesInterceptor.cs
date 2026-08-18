using System.Text.Json;
using AssetDesk.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AssetDesk.Api.Data;

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
/// Trade-off, accepted deliberately: with no caller-managed transaction in play, audit rows
/// commit in a second transaction, issued after the business transaction has already
/// committed. If that second save fails, the business change stands but its audit trail does
/// not — the failure is logged at error level and swallowed rather than rethrown, so a lost
/// audit row never turns into a duplicate ticket or asset from a client retrying an operation
/// that actually succeeded. Making both writes commit atomically would mean this interceptor
/// manages a transaction spanning both saves; that is a real gap, left as a deliberate open
/// follow-up rather than solved here.
///
/// That reasoning does not hold inside a caller-managed transaction (see
/// TicketService.FulfilAsync). There, SavedChanges fires before the caller's Commit, so the
/// audit save joins the caller's transaction rather than following it — and on PostgreSQL a
/// failed statement aborts the whole transaction block, where COMMIT then silently rolls back
/// and reports success. Swallowing there would let a caller report success for an operation
/// that persisted nothing. So when DbContext.Database.CurrentTransaction is not null the
/// exception is rethrown, and the caller's own catch decides to roll back deliberately.
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
            if (!TryHandleFailure(context, logs, ex)) throw;
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
            if (!TryHandleFailure(context, logs, ex)) throw;
        }
        finally
        {
            _writing = false;
        }
    }

    /// <summary>
    /// Handles a failed audit save. Returns true when the failure was absorbed, false when the
    /// caller must rethrow it.
    ///
    /// With no caller-managed transaction the audit save follows an already-committed business
    /// change, so failing the caller's operation would be worse than losing the audit row:
    /// detach, log, swallow. Inside a caller-managed transaction the business change has NOT
    /// committed yet and the failed statement may have poisoned that transaction, so the
    /// failure has to reach the caller — it still detaches and logs, then reports "not handled"
    /// so the exception propagates. This is the last line of defence and must not itself throw.
    /// </summary>
    private bool TryHandleFailure(DbContext context, List<AuditLog> logs, Exception ex)
    {
        // Attempt to detach each log independently so a context.Entry() failure doesn't
        // prevent other detaches or the logging attempt.
        foreach (var log in logs)
        {
            try
            {
                context.Entry(log).State = EntityState.Detached;
            }
            catch
            {
                // Swallow detach failures; we are already handling a failure and must not rethrow.
            }
        }

        var insideCallerTransaction = false;
        try
        {
            insideCallerTransaction = context.Database.CurrentTransaction is not null;
        }
        catch
        {
            // Reading the ambient transaction must never be the thing that throws here; on
            // doubt, fall back to the swallowing behaviour that cannot fail the caller.
        }

        // Attempt to log the loss independently so a logger failure doesn't prevent detaches.
        try
        {
            var entityTypes = string.Join(", ", logs.Select(l => l.EntityType).Distinct());
            if (insideCallerTransaction)
            {
                _logger.LogError(ex,
                    "Failed to persist {Count} audit log entries for entity types [{EntityTypes}] " +
                    "inside a caller-managed transaction. Rethrowing so the caller rolls back: the " +
                    "business change has not committed and the transaction may already be aborted.",
                    logs.Count, entityTypes);
            }
            else
            {
                _logger.LogError(ex,
                    "Failed to persist {Count} audit log entries for entity types [{EntityTypes}]. " +
                    "The underlying business change already committed; this audit evidence is lost.",
                    logs.Count, entityTypes);
            }
        }
        catch
        {
            // Swallow logger failures; we are already handling a failure and must not rethrow.
        }

        return !insideCallerTransaction;
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
