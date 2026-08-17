using barakoCMS.Models;

namespace BarakoCMS.Tests.Builders;

/// <summary>
/// Builds a <see cref="User"/>.
///
/// 29 hand-written instances, and the trap in most of them is the password: several seed a user with
/// no <c>PasswordHash</c> at all, which is fine until a test tries to sign in and gets a failure that
/// looks like broken authentication rather than a fixture that was never valid. This always hashes a
/// password, so a built user can always log in.
/// </summary>
public sealed class UserBuilder : BuilderBase<User>
{
    /// <summary>Meets the 12-character policy, so a built user can register and sign in.</summary>
    public const string DefaultPassword = "P@ssword123!Ab";

    private readonly List<Guid> _roleIds = new();
    private string? _username;
    private string? _email;
    private string _password = DefaultPassword;
    private Guid? _id;
    private int _failedAttempts;
    private DateTime? _lockoutUntil;

    public UserBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    public UserBuilder Named(string username)
    {
        _username = username;
        return this;
    }

    public UserBuilder WithEmail(string email)
    {
        _email = email;
        return this;
    }

    public UserBuilder WithPassword(string password)
    {
        _password = password;
        return this;
    }

    public UserBuilder InRole(Guid roleId)
    {
        _roleIds.Add(roleId);
        return this;
    }

    /// <summary>Locked out, for asserting that a gate refuses before doing any work.</summary>
    public UserBuilder LockedOut(TimeSpan? forHowLong = null)
    {
        _failedAttempts = 5;
        _lockoutUntil = DateTime.UtcNow.Add(forHowLong ?? TimeSpan.FromMinutes(15));
        return this;
    }

    public UserBuilder WithFailedAttempts(int attempts)
    {
        _failedAttempts = attempts;
        return this;
    }

    public override User Build()
    {
        var username = _username ?? Unique("user");
        return new User
        {
            Id = _id ?? Guid.NewGuid(),
            Username = username,
            Email = _email ?? $"{username}@test.local",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(_password),
            RoleIds = _roleIds,
            FailedLoginAttempts = _failedAttempts,
            LockoutUntil = _lockoutUntil,
            CreatedAt = DateTime.UtcNow,
        };
    }
}
