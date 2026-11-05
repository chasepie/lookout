using System.Globalization;

namespace ChasePie.Lookout.Backend;

/// <summary>
/// Matches route values outside <c>/api</c>, so the frontend's catch-all page leaves API routes to routing.
/// </summary>
public sealed class NotApiRouteConstraint : IRouteConstraint
{
    public const string Name = "notapi";

    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        var value = values.TryGetValue(routeKey, out var routeValue)
            ? Convert.ToString(routeValue, CultureInfo.InvariantCulture)
            : null;
        return value is null
            || !(value.Equals("api", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("api/", StringComparison.OrdinalIgnoreCase));
    }
}
