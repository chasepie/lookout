using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace ChasePie.Lookout.Backend.Controllers;

public class LookoutControllerBase : ControllerBase
{
    public ObjectResult Problem(string detail, HttpStatusCode statusCode, string? instance = null, string? title = null, string? type = null, IDictionary<string, object?>? extensions = null)
    {
        return base.Problem(detail, instance, (int)statusCode, title, type, extensions);
    }

    public ObjectResult Problem(string detail, HttpStatusCode statusCode, string? instance, string? title, string? type)
    {
        return base.Problem(detail, instance, (int)statusCode, title, type);
    }
}
