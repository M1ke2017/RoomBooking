using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CrewCall.Api.Tests;

/// <summary>
/// Counts the SQL commands EF Core executes inside one async flow (other tests running in parallel are not counted),
/// to detect N+1 query patterns.
/// </summary>
internal sealed class SqlCommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private static readonly AsyncLocal<StrongBox<int>?> _current = new();
    private static readonly SqlCommandCounter _instance = new();

    private SqlCommandCounter() => DiagnosticListener.AllListeners.Subscribe(this);

    /// <summary>The number of SQL commands <paramref name="action"/> executes.</summary>
    public static async Task<int> CountAsync(Func<Task> action)
    {
        GC.KeepAlive(_instance);
        var counter = new StrongBox<int>();
        _current.Value = counter;
        try
        {
            await action();
        }
        finally
        {
            _current.Value = null;
        }

        return counter.Value;
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == DbLoggerCategory.Name)
        {
            listener.Subscribe(this);
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> diagnostic)
    {
        if (diagnostic.Key == RelationalEventId.CommandExecuted.Name && _current.Value is { } counter)
        {
            Interlocked.Increment(ref counter.Value);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted()
    {
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
    {
    }
}
