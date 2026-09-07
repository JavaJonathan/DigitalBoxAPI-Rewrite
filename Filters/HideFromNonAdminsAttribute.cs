using DigitalBoxApi.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DigitalBoxApi.Filters;

// Admin-only, but invisible to everyone else. [Authorize] on the controller already 401s
// anonymous requests (the SPA turns that into a login redirect), so this filter only runs for
// authenticated users: anyone who isn't an admin gets 404, not 403 — the feature's existence
// is not disclosed to curious warehouse staff. A signed-in user probing /api/lookup/* sees
// exactly what they'd get for a misspelled route.
//
// Scope note: UsersController and POST /api/orders/upload still return 403 for non-admins by
// design; only the lookup feature is deliberately hidden.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class HideFromNonAdminsAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.User.IsInRole(nameof(UserRole.Admin)))
        {
            // Write a bare 404 directly rather than via NotFoundResult / StatusCodeResult —
            // [ApiController]'s client-error filter would wrap those in a ProblemDetails body,
            // which an unmatched route doesn't return. This keeps the two responses identical.
            context.HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Result = new EmptyResult();
        }

        return Task.CompletedTask;
    }
}
