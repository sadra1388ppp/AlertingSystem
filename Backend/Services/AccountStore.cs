using System.Data.Common;
using AlertingSystem.Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace AlertingSystem.Backend.Services;

public sealed class AccountStore
{
    private readonly AlertingDbContextFactory _contextFactory;

    public AccountStore(AlertingDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public bool HasAccounts()
    {
        using var db = _contextFactory.CreateContext();
        return db.Accounts.AsNoTracking().Any(account => !account.IsDeleted);
    }

    public List<AccountUser> GetAllAccounts()
    {
        using var db = _contextFactory.CreateContext();
        return db.Accounts
            .AsNoTracking()
            .OrderBy(account => account.IsDeleted)
            .ThenByDescending(account => account.CreatedAt)
            .ThenByDescending(account => account.Id)
            .ToList();
    }

    public bool IsActiveAccount(int userId)
    {
        using var db = _contextFactory.CreateContext();
        return db.Accounts.Any(account =>
            account.Id == userId && !account.IsDeleted);
    }

    public bool TryCreateAccount(
        string displayName,
        string username,
        string password,
        out AccountUser? createdUser,
        out string error)
    {
        createdUser = null;
        error = string.Empty;
        displayName = (displayName ?? string.Empty).Trim();
        username = NormalizeUsername(username);

        if (displayName.Length is < 2 or > 100)
        {
            error = "Display name must contain between 2 and 100 characters.";
            return false;
        }

        if (username.Length is < 3 or > 32 ||
            username.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character != '.' && character != '_' && character != '-'))
        {
            error = "Username must be 3–32 characters and use only letters, numbers, dots, underscores, or hyphens.";
            return false;
        }

        if (password.Length is < 8 or > 1024)
        {
            error = "Password must contain at least 8 characters.";
            return false;
        }

        using var db = _contextFactory.CreateContext();

        if (db.Accounts.Any(account => account.Username == username))
        {
            error = "That username is already registered. Choose another username.";
            return false;
        }

        var account = new AccountUser
        {
            DisplayName = displayName,
            Username = username,
            PasswordHash = AccountPasswordHasher.Create(password),
            CreatedAt = DateTime.UtcNow
        };

        db.Accounts.Add(account);

        try
        {
            db.SaveChanges();
            createdUser = account;
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            error = "That username is already registered. Choose another username.";
            return false;
        }
    }

    public bool TryAuthenticate(string username, string password, out AccountUser? user)
    {
        user = null;
        username = NormalizeUsername(username);

        using var db = _contextFactory.CreateContext();
        var account = db.Accounts
            .AsNoTracking()
            .FirstOrDefault(item =>
                item.Username == username && !item.IsDeleted);

        if (account is null ||
            !AccountPasswordHasher.Verify(password, account.PasswordHash))
        {
            return false;
        }

        user = account;
        return true;
    }

    public bool TrySoftDeleteAccount(int userId, string password, out string error)
    {
        error = string.Empty;

        using var db = _contextFactory.CreateContext();
        var account = db.Accounts.FirstOrDefault(item => item.Id == userId);

        if (account is null)
        {
            error = "This account could not be found in MariaDB.";
            return false;
        }

        if (account.IsDeleted)
        {
            error = "This account has already been deleted.";
            return false;
        }

        if (!AccountPasswordHasher.Verify(password, account.PasswordHash))
        {
            error = "The password is incorrect. The account was not changed.";
            return false;
        }

        account.IsDeleted = true;
        account.DeletedAt = DateTime.UtcNow;

        if (db.SaveChanges() != 1)
        {
            error = "The account could not be marked as deleted. Refresh the account list and try again.";
            return false;
        }

        return true;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is DbException databaseException &&
        string.Equals(databaseException.SqlState, "23000", StringComparison.Ordinal);

    private static string NormalizeUsername(string? username) =>
        (username ?? string.Empty).Trim().ToLowerInvariant();
}