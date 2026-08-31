using System.Net;
using System.Net.Http.Json;
using GM.KYC;
using GM.KYC.Domain;
using GM.Testing;
using GM.Testing.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GM.KYC.Sample.Tests;

// Drives the sample KYC API end to end with a fake IKycProvider swapped in (no real Identomat call),
// demonstrating the GM.Testing WebApplicationFactory + the KYC HTTP surface.
public class KycSampleTests
{
    private sealed class FakeProvider : IKycProvider
    {
        public string Name => "Fake";
        public Task<ProviderSession> BeginAsync(InitiateVerificationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderSession { ProviderSessionId = "SES-1", VerificationUrl = "https://widget.identomat.com/?session_token=demo" });
        public Task AttachDocumentAsync(ProviderDocumentSubmission submission, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AttachLivenessAsync(ProviderLivenessSubmission submission, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<VerificationResult> GetResultAsync(string providerSessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult { Status = VerificationStatus.Approved });
    }

    private sealed class FakeWebhookProcessor : IKycWebhookProcessor
    {
        public Task<KycWebhookOutcome> ProcessAsync(KycWebhookRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KycWebhookOutcome { Handled = true, ProviderSessionId = "SES-1", Status = VerificationStatus.Approved });
    }

    private static GmWebApplicationFactory<Program> Factory() =>
        new GmWebApplicationFactory<Program>().WithServices(s =>
        {
            s.RemoveAll<IKycProvider>();
            s.AddScoped<IKycProvider, FakeProvider>();
            s.RemoveAll<IKycWebhookProcessor>();
            s.AddScoped<IKycWebhookProcessor, FakeWebhookProcessor>();
        });

    [Fact]
    public async Task Start_session_returns_widget_url()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/kyc-sessions", new { applicantReference = "user-1" });
        var body = await response.ShouldBeOkAsync<SessionDto>();

        Assert.Equal("SES-1", body.SessionId);
        Assert.Contains("identomat.com", body.VerificationUrl);
        Assert.Equal("Pending", body.Status);
    }

    [Fact]
    public async Task Get_status_maps_provider_result()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/kyc-sessions/SES-1");
        var body = await response.ShouldBeOkAsync<StatusDto>();

        Assert.Equal("Approved", body.Status);
    }

    [Fact]
    public async Task Webhook_endpoint_processes_callback()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/kyc-webhooks/identomat",
            new StringContent("""{"session":"SES-1"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ShouldBeOkAsync<WebhookDto>();
        Assert.True(body.Handled);
    }

    private sealed record SessionDto(string SessionId, string VerificationUrl, string Status);
    private sealed record StatusDto(string Status, string? RejectionCode, string? RejectionReason);
    private sealed record WebhookDto(bool Handled, bool Duplicate, string? ProviderSessionId, string? Status);
}
