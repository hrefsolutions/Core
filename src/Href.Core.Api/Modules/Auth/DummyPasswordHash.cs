using Href.Core.Api.Modules.Users;
using Microsoft.AspNetCore.Identity;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// A password hash made once with the registered <see cref="IPasswordHasher{TUser}"/>, verified on the login paths that
/// have no user (unknown or malformed email, empty password), so they cost one hash verification like a wrong password.
/// </summary>
public sealed class DummyPasswordHash(IPasswordHasher<User> passwordHasher)
{
    private const string Password = "timing-equalizer-password";

    private readonly Lazy<string> _hash = new(() => passwordHasher.HashPassword(null!, Password));

    public void Verify(string? password) =>
        passwordHasher.VerifyHashedPassword(null!, _hash.Value, string.IsNullOrEmpty(password) ? Password : password);
}
