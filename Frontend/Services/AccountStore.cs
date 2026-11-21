using AlertingSystem.Contracts;
using AlertingSystem.Models;

namespace AlertingSystem.Services;

public sealed class AccountStore
{
    public bool HasAccounts() => BackendApiClient.Get<bool>("api/accounts/exists");

    public List<AccountUser> GetAllAccounts() =>
        BackendApiClient.Get<List<AccountDto>>("api/accounts").Select(FromDto).ToList();

    public bool IsActiveAccount(int userId) =>
        BackendApiClient.Get<bool>($"api/accounts/{userId}/active");

    public bool TryAuthenticate(string username, string password, out AccountUser? user)
    {
        var result = BackendApiClient.Post<AuthenticationResponse>(
            "api/accounts/authenticate",
            new LoginRequest { Username = username, Password = password });

        user = result.User is null ? null : FromDto(result.User);
        return result.Success;
    }

    public bool TryCreateAccount(
        string displayName,
        string username,
        string password,
        out AccountUser? createdUser,
        out string error)
    {
        var result = BackendApiClient.Post<CreateAccountResponse>(
            "api/accounts",
            new CreateAccountRequest
            {
                DisplayName = displayName,
                Username = username,
                Password = password
            });

        createdUser = result.User is null ? null : FromDto(result.User);
        error = result.Error;
        return result.Success;
    }

    public bool TrySoftDeleteAccount(int userId, string password, out string error)
    {
        var result = BackendApiClient.Post<OperationResponse>(
            $"api/accounts/{userId}/delete",
            new DeleteAccountRequest { Password = password });

        error = result.Error;
        return result.Success;
    }

    private static AccountUser FromDto(AccountDto item) => new()
    {
        Id = item.Id,
        DisplayName = item.DisplayName,
        Username = item.Username,
        CreatedAt = item.CreatedAt,
        IsDeleted = item.IsDeleted,
        DeletedAt = item.DeletedAt
    };
}
