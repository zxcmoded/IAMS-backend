using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Inventory;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Scanning.ResolveScan;

// ── Contract ────────────────────────────────────────────────────────────────
/// <param name="RawCode">The raw scanned/typed code to resolve.</param>
/// <param name="DeviceId">Optional device that produced the scan (audit).</param>
/// <param name="ScannedAtUtc">Optional client-side scan time; server time is used when absent.</param>
/// <param name="IdempotencyKey">
/// Optional client de-duplication key so a replayed queued scan-event isn't logged twice. When present and
/// already seen, the original resolution is returned unchanged.
/// </param>
public record ResolveScanCommand(
    string RawCode,
    string? DeviceId,
    DateTime? ScannedAtUtc,
    string? IdempotencyKey);

/// <param name="ResolvedType">Sku | Location | Asset | NoMatch | Blocked.</param>
/// <param name="ResolvedEntityId">
/// Id of the resolved entity for Sku/Location/Asset; <c>null</c> for NoMatch and (deliberately, BR-003) Blocked.
/// </param>
/// <param name="Label">Human label for the resolved entity when known (item name or bin name); null otherwise.</param>
/// <param name="ScanEventId">Id of the persisted <see cref="ScanEvent"/> audit row.</param>
public record ResolveScanResponse(
    string ResolvedType,
    Guid? ResolvedEntityId,
    string? Label,
    Guid ScanEventId);

public class ResolveScanValidator : AbstractValidator<ResolveScanCommand>
{
    public ResolveScanValidator()
    {
        RuleFor(x => x.RawCode).NotEmpty().MaximumLength(400);
        RuleFor(x => x.DeviceId).MaximumLength(200);
        RuleFor(x => x.IdempotencyKey).MaximumLength(200);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Resolves a raw scanned code to an inventory item (by SKU or barcode) or a physical location (a
/// <see cref="Bin"/> matched by its label) within the caller's reachable company set, and records a
/// <see cref="ScanEvent"/> for every attempt.
///
/// Tenant isolation (BR-003) is a first-class outcome: a code that matches a real entity OUTSIDE the reachable
/// set resolves to <see cref="ScanResolvedType.Blocked"/> with a NULL entity id — the other tenant's id is
/// never returned or persisted, so neither the response nor the audit log can leak it. That is kept distinct
/// from <see cref="ScanResolvedType.NoMatch"/> (matched nothing anywhere reachable-or-not).
///
/// <see cref="ScanResolvedType.Asset"/> is reserved for F5: there is no Fixed Assets table to match against
/// yet, so the current resolver never emits it — the value exists in the contract so clients can handle it
/// without a breaking change when F5 lands. (Flagged to the Lead as a deliberate scope decision.)
/// </summary>
public class ResolveScanHandler(IamsDbContext db, AccessCheckService accessCheck, ICurrentUser currentUser, IClock clock)
{
    public async Task<Results<Ok<ResolveScanResponse>, ProblemHttpResult>> HandleAsync(
        ResolveScanCommand command, CancellationToken ct)
    {
        // Idempotent replay: a queued scan re-delivered with the same key returns its original outcome.
        if (!string.IsNullOrEmpty(command.IdempotencyKey))
        {
            var existing = await db.ScanEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.IdempotencyKey == command.IdempotencyKey, ct);
            if (existing is not null)
            {
                return TypedResults.Ok(await BuildResponseAsync(existing, ct));
            }
        }

        var code = command.RawCode.Trim();
        var reachable = (await accessCheck.GetReachableCompanyIdsAsync(ct)).ToArray();

        var (resolvedType, resolvedEntityId, label) = await ResolveAsync(code, reachable, ct);

        var scanEvent = new ScanEvent
        {
            Id = Guid.NewGuid(),
            TenantId = currentUser.TenantId,
            CompanyId = currentUser.CompanyId,
            ScannedByUserId = currentUser.UserId,
            RawCode = code,
            ResolvedType = resolvedType,
            ResolvedEntityId = resolvedEntityId, // NULL for NoMatch and Blocked — never leak a cross-tenant id
            DeviceId = command.DeviceId,
            ScannedAtUtc = command.ScannedAtUtc ?? clock.UtcNow.UtcDateTime,
            IdempotencyKey = string.IsNullOrEmpty(command.IdempotencyKey) ? null : command.IdempotencyKey
        };
        db.ScanEvents.Add(scanEvent);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            command.IdempotencyKey is not null &&
            InventoryConcurrency.IsUniqueViolation(ex, "IdempotencyKey"))
        {
            // A concurrent replay of the same key won the insert race — return that original outcome.
            db.Entry(scanEvent).State = EntityState.Detached;
            var winner = await db.ScanEvents.AsNoTracking()
                .FirstAsync(e => e.IdempotencyKey == command.IdempotencyKey, ct);
            return TypedResults.Ok(await BuildResponseAsync(winner, ct));
        }

        return TypedResults.Ok(new ResolveScanResponse(
            resolvedType.ToString(), resolvedEntityId, label, scanEvent.Id));
    }

    private async Task<(ScanResolvedType Type, Guid? EntityId, string? Label)> ResolveAsync(
        string code, Guid[] reachable, CancellationToken ct)
    {
        // 1) SKU / barcode within the reachable set.
        var item = await db.InventoryItems.AsNoTracking()
            .Where(i => reachable.Contains(i.CompanyId) && i.IsActive && (i.Sku == code || i.Barcode == code))
            .Select(i => new { i.Id, i.Name })
            .FirstOrDefaultAsync(ct);
        if (item is not null)
        {
            return (ScanResolvedType.Sku, item.Id, item.Name);
        }

        // 2) Physical location: a Bin matched by its label within the reachable set.
        var bin = await db.Bins.AsNoTracking()
            .Where(b => reachable.Contains(b.CompanyId) && b.IsActive && b.Name == code)
            .Select(b => new { b.Id, b.Name })
            .FirstOrDefaultAsync(ct);
        if (bin is not null)
        {
            return (ScanResolvedType.Location, bin.Id, bin.Name);
        }

        // 3) BR-003: matched a real entity OUTSIDE the reachable set → Blocked, and never surface its id.
        var existsElsewhere =
            await db.InventoryItems.AsNoTracking().AnyAsync(i => i.Sku == code || i.Barcode == code, ct) ||
            await db.Bins.AsNoTracking().AnyAsync(b => b.Name == code, ct);
        return existsElsewhere
            ? (ScanResolvedType.Blocked, (Guid?)null, null)
            : (ScanResolvedType.NoMatch, (Guid?)null, null);
    }

    /// <summary>Rebuilds the response for an already-persisted scan event (idempotent replay), re-deriving the label.</summary>
    private async Task<ResolveScanResponse> BuildResponseAsync(ScanEvent e, CancellationToken ct)
    {
        string? label = null;
        if (e.ResolvedEntityId is Guid id)
        {
            label = e.ResolvedType switch
            {
                ScanResolvedType.Sku => await db.InventoryItems.AsNoTracking()
                    .Where(i => i.Id == id).Select(i => i.Name).FirstOrDefaultAsync(ct),
                ScanResolvedType.Location => await db.Bins.AsNoTracking()
                    .Where(b => b.Id == id).Select(b => b.Name).FirstOrDefaultAsync(ct),
                _ => null
            };
        }

        return new ResolveScanResponse(e.ResolvedType.ToString(), e.ResolvedEntityId, label, e.Id);
    }
}
