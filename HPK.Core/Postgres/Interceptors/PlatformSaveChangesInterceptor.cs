using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using HPK.Core.Utils.Services;

namespace HPK.Core.Postgres.Interceptors;

public class PlatformSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PlatformSaveChangesInterceptor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context == null) return result;

        var entries = eventData.Context.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)
            .ToList();

        if (!entries.Any()) return result;

        var accountIdHeader = _httpContextAccessor.HttpContext?.Request.Headers["x-account"].ToString();
        var currentAccountId = string.IsNullOrWhiteSpace(accountIdHeader) ? (string?)null : accountIdHeader;

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                var createdByProp = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "CreatedBy");
                if (createdByProp != null && currentAccountId != null)
                {
                    createdByProp.CurrentValue = currentAccountId;
                }
            }

            if (entry.State == EntityState.Modified)
            {
                var updatedByProp = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "UpdatedBy");
                if (updatedByProp != null && currentAccountId != null)
                {
                    updatedByProp.CurrentValue = currentAccountId;
                }
            }

            var updatedAtProp = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "UpdatedAt");
            if (updatedAtProp != null && entry.State == EntityState.Modified)
            {
                updatedAtProp.CurrentValue = DateTime.UtcNow;
            }
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
