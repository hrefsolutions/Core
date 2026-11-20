using Href.Core.Api.Common.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Href.Core.Api.Modules.Users;

/// <summary>
/// Admin endpoints for global users. There is no DELETE: users are locked, never deleted.
/// Public until admin authentication exists (#53).
/// </summary>
[ApiController]
[Route("api/users")]
public sealed class UsersController(UserService userService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await userService.CreateAsync(request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = UserService.DefaultPageSize)
    {
        var result = await userService.ListAsync(new ListUsersQuery(search, status, page, pageSize), cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await userService.GetByIdAsync(id, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await userService.UpdateAsync(id, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpPost("{id:guid}/lock")]
    public async Task<IActionResult> Lock(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] LockUserRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await userService.LockAsync(id, request, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }

    [HttpPost("{id:guid}/unlock")]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken cancellationToken)
    {
        var result = await userService.UnlockAsync(id, cancellationToken);

        return result.IsFailure
            ? result.ToProblem(this)
            : Ok(result.Value);
    }
}
