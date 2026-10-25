using System.Linq.Expressions;
using ChasePie.Lookout.Model;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Running;

/// <summary>
/// A Watch's status, which follows from what its Runs left on it. A failure streak outranks Pending, since a Watch whose
/// Script never Succeeded needs attention too.
/// </summary>
public static class WatchStatuses
{
    /// <summary>A Watch's status, as an expression rather than a method, so EF translates it to SQL.</summary>
    public static readonly Expression<Func<Watch, WatchStatus>> Projection = watch =>
        watch.IsPaused
            ? WatchStatus.Paused
            : watch.FailureStreak > 0
                ? WatchStatus.Failing
                : watch.Baseline == null
                    ? WatchStatus.Pending
                    : WatchStatus.Healthy;

    private static readonly Func<Watch, WatchStatus> CompiledProjection = Projection.Compile();

    public static WatchStatus Of(Watch watch) => CompiledProjection(watch);

    /// <summary>The Watch's status as stored, or null if it doesn't exist.</summary>
    public static Task<WatchStatus?> ReadAsync(IQueryable<Watch> watches, Guid id, CancellationToken cancellationToken) =>
        watches.Where(watch => watch.Id == id)
            .Select(Projection)
            .Select(status => (WatchStatus?)status)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Puts <see cref="Projection"/> in the shape's status, so EF translates the whole of a projection using it.</summary>
    public static Expression<Func<Watch, T>> WithStatus<T>(Expression<Func<Watch, WatchStatus, T>> shape)
    {
        var watch = shape.Parameters[0];
        var status = new ReplaceParameter(Projection.Parameters[0], watch)
            .Visit(Projection.Body);
        return Expression.Lambda<Func<Watch, T>>(
            new ReplaceParameter(shape.Parameters[1], status).Visit(shape.Body), watch);
    }

    private sealed class ReplaceParameter(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : node;
    }
}
