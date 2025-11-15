#pragma warning disable CA1873

using System;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Jellyfin.Database.Implementations.Locking;

/// <summary>
/// Defines a locking mechanism that will retry any write operation for a few times.
/// </summary>
public class OptimisticLockBehavior : IEntityFrameworkCoreLockingBehavior
{
    private readonly TimeSpan _medianFirstRetryDelay;
    private readonly TimeSpan _maxDelay;
    private readonly int _maxRetries;
    private readonly Func<Exception?, bool> _shouldRetry;
    private readonly ILogger<OptimisticLockBehavior> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ResiliencePipeline _pipeline;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimisticLockBehavior"/> class.
    /// </summary>
    /// <param name="medianFirstRetryDelay">The median first delay for retry backoff.</param>
    /// <param name="maxDelay">The maximum delay between retries.</param>
    /// <param name="maxRetries">The maximum number of retries.</param>
    /// <param name="shouldRetry">Function to determine if an exception should be retried.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public OptimisticLockBehavior(TimeSpan medianFirstRetryDelay, TimeSpan maxDelay, int maxRetries, Func<Exception?, bool> shouldRetry, ILoggerFactory loggerFactory)
    {
        _medianFirstRetryDelay = medianFirstRetryDelay;
        _maxDelay = maxDelay;
        _maxRetries = maxRetries;
        _shouldRetry = shouldRetry;
        _loggerFactory = loggerFactory;
        _logger = _loggerFactory.CreateLogger<OptimisticLockBehavior>();

        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = args => new ValueTask<bool>(_shouldRetry(args.Outcome.Exception)),
                MaxRetryAttempts = _maxRetries,
                Delay = _medianFirstRetryDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxDelay = _maxDelay,
                OnRetry = args =>
                {
                    if (args.Outcome.Exception is Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Database operation failed, will retry attempt {RetryAttempt} in {Delay}ms. Exception: {Message}",
                            args.AttemptNumber + 1,
                            args.RetryDelay.TotalMilliseconds,
                            ex.Message);
                    }

                    return default;
                }
            })
            .Build();
    }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder optionsBuilder)
    {
        _logger.LogInformation("The database locking mode has been set to: Optimistic.");
        optionsBuilder.AddInterceptors(new RetryInterceptor(_pipeline, _loggerFactory.CreateLogger<RetryInterceptor>()));
        optionsBuilder.AddInterceptors(new TransactionLockingInterceptor(_pipeline, _loggerFactory.CreateLogger<TransactionLockingInterceptor>()));
    }

    /// <inheritdoc/>
    public void OnSaveChanges(JellyfinDbContext context, Action saveChanges)
    {
        _pipeline.Execute(saveChanges);
    }

    /// <inheritdoc/>
    public async Task OnSaveChangesAsync(JellyfinDbContext context, Func<Task> saveChanges)
    {
        await _pipeline.ExecuteAsync(async _ => await saveChanges().ConfigureAwait(false)).ConfigureAwait(false);
    }

    private sealed class TransactionLockingInterceptor : DbTransactionInterceptor
    {
        private readonly ResiliencePipeline _pipeline;

        private readonly ILogger _logger;

        public TransactionLockingInterceptor(ResiliencePipeline pipeline, ILogger logger)
        {
            _pipeline = pipeline;
            _logger = logger;
        }

        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            return InterceptionResult<DbTransaction>.SuppressWithResult(_pipeline.Execute(() => connection.BeginTransaction(eventData.IsolationLevel)));
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            return InterceptionResult<DbTransaction>.SuppressWithResult(await _pipeline.ExecuteAsync(async _ => await connection.BeginTransactionAsync(eventData.IsolationLevel, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
        }
    }

    private sealed class RetryInterceptor : DbCommandInterceptor
    {
        private readonly ResiliencePipeline _pipeline;

        private readonly ILogger _logger;

        public RetryInterceptor(ResiliencePipeline pipeline, ILogger logger)
        {
            _pipeline = pipeline;
            _logger = logger;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            return InterceptionResult<int>.SuppressWithResult(_pipeline.Execute(command.ExecuteNonQuery));
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            return InterceptionResult<int>.SuppressWithResult(await _pipeline.ExecuteAsync(async _ => await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            return InterceptionResult<object>.SuppressWithResult(_pipeline.Execute(() => command.ExecuteScalar()!));
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            return InterceptionResult<object>.SuppressWithResult((await _pipeline.ExecuteAsync(async _ => await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))!);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(_pipeline.Execute(command.ExecuteReader));
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(await _pipeline.ExecuteAsync(async _ => await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
        }
    }
}
