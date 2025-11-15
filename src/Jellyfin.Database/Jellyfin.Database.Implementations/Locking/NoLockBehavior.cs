using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Implementations.Locking;

/// <summary>
/// Default lock behavior. Defines no explicit application locking behavior.
/// </summary>
public class NoLockBehavior : IEntityFrameworkCoreLockingBehavior
{
    private readonly ILogger<NoLockBehavior> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoLockBehavior"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    public NoLockBehavior(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<NoLockBehavior>();
    }

    /// <inheritdoc/>
    public void OnSaveChanges(JellyfinDbContext context, Action saveChanges)
    {
        saveChanges();
    }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder optionsBuilder)
    {
        _logger.LogInformation("The database locking mode has been set to: NoLock.");
    }

    /// <inheritdoc/>
    public async Task OnSaveChangesAsync(JellyfinDbContext context, Func<Task> saveChanges)
    {
        await saveChanges().ConfigureAwait(false);
    }
}
