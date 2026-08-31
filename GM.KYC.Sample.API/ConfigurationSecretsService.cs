using GM.Secrets;

namespace GM.KYC.Sample.API;

/// <summary>
/// A minimal <see cref="ISecretsService"/> for the sample that reads from configuration/environment
/// (e.g. <c>Secrets:Identomat:CompanyKey</c>, or the env var <c>Secrets__Identomat__CompanyKey</c>).
/// In production register a real GM.Secrets provider (Azure Key Vault, AWS Secrets Manager, …) instead
/// — the KYC library only depends on <see cref="ISecretsService"/>.
/// </summary>
internal sealed class ConfigurationSecretsService(IConfiguration configuration) : ISecretsService
{
    public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(configuration[$"Secrets:{name}"] ?? configuration[name]);

    public Task<T?> GetSecretAsync<T>(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(configuration.GetSection($"Secrets:{name}").Get<T>());

    public Task<string> GetRequiredSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        var value = configuration[$"Secrets:{name}"] ?? configuration[name];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Secret '{name}' is not configured.")
            : Task.FromResult(value);
    }
}
