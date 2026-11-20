using Href.Core.Api.Common.Results;

namespace Href.Core.Api.Modules.Users;

public static class UserErrors
{
    public static readonly Error InvalidEmail = Error.Validation(
        "Users.InvalidEmail",
        "The email must be a single valid address of up to 254 characters.");

    public static readonly Error InvalidFullName = Error.Validation(
        "Users.InvalidFullName",
        "The full name is required, must be a single line and must be between 1 and 150 characters.");

    public static readonly Error InvalidLockReason = Error.Validation(
        "Users.InvalidLockReason",
        "The lock reason cannot exceed 500 characters or contain NUL characters.");

    public static readonly Error WeakPassword = Error.Validation(
        "Users.WeakPassword",
        "The password must be between 10 and 128 characters and cannot be only whitespace.");

    public static readonly Error EmailTaken = Error.Conflict(
        "Users.EmailTaken",
        "A user with the specified email already exists.");

    public static readonly Error EmailAlreadyVerified = Error.Conflict(
        "Users.EmailAlreadyVerified",
        "The email of the user is already verified.");

    public static readonly Error AlreadyLocked = Error.Conflict(
        "Users.AlreadyLocked",
        "The user is already locked.");

    public static readonly Error NotLocked = Error.Conflict(
        "Users.NotLocked",
        "The user is not locked.");

    public static readonly Error NotFound = Error.NotFound(
        "Users.NotFound",
        "The requested user was not found.");

    public static readonly Error InvalidStatus = Error.Validation(
        "Users.InvalidStatus",
        "The user status must be 'Active' or 'Locked'.");

    public static readonly Error InvalidSearch = Error.Validation(
        "Users.InvalidSearch",
        "The search text must be a single line of up to 254 characters.");

    public static readonly Error InvalidPage = Error.Validation(
        "Users.InvalidPage",
        "The page must be 1 or greater.");

    public static readonly Error InvalidPageSize = Error.Validation(
        "Users.InvalidPageSize",
        "The page size must be between 1 and 200.");
}
