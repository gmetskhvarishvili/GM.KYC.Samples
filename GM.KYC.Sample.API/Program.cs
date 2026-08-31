using GM.Caching;
using GM.DistributedLock;
using GM.FileStorage;
using GM.FileStorage.Local;
using GM.Idempotency;
using GM.KYC;
using GM.KYC.Domain;
using GM.KYC.Identomat;
using GM.KYC.Persistence;
using GM.KYC.Sample.API;
using GM.Secrets;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Infra the KYC provider relies on: idempotency (webhook dedup) + a secrets service + file storage.
builder.Services.AddGMCaching();
builder.Services.AddGMDistributedLock();
builder.Services.AddGMIdempotency();
builder.Services.AddSingleton<ISecretsService, ConfigurationSecretsService>(); // demo: reads config; use a real GM.Secrets provider in prod
builder.Services.AddGMFileStorage(o => o.Provider = "Local");                   // selector → the Local backend below
builder.Services.AddGMLocalFileStorage(o => o.RootPath = Path.Combine(Path.GetTempPath(), "gm-kyc-sample"));

// The KYC stack: provider-agnostic core → Identomat provider → audit-grade persistence.
// A second provider later would be another .AddXyz() call here, not a rename of AddGMKyc.
builder.Services
    .AddGMKyc(o => o.MaxImageDimension = 2000)
    .AddIdentomat(o =>
    {
        o.CompanyKeySecretName = "Identomat:CompanyKey";
        o.WebhookSecretName = "Identomat:WebhookSecret";
    })
    .AddKycPersistence(o => o.UseInMemoryDatabase("kyc-sample")); // demo store; use Npgsql/SqlServer in prod

builder.Services.AddHealthChecks();

var app = builder.Build();

// Liveness must not depend on downstream dependencies, so it runs no checks; readiness runs
// every registered health check (none here yet). See engineering baseline §11.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

app.MapGet("/", () => Results.Ok(new
{
    message = "GM.KYC sample — Identomat verification",
    endpoints = Endpoints,
}));

// Start a verification. The response carries the hosted-widget URL to redirect the applicant to.
app.MapPost("/api/v1/kyc-sessions", async (StartSessionRequest body, IKycVerificationService kyc, CancellationToken ct) =>
{
    var session = await kyc.InitiateVerificationAsync(
        new InitiateVerificationRequest { ApplicantReference = body.ApplicantReference }, ct);
    return Results.Ok(new { sessionId = session.ProviderSessionId, verificationUrl = session.VerificationUrl, status = session.Status.ToString() });
});

// Submit a document image — normalized (resize + EXIF/GPS strip) and stored before submission.
app.MapPost("/api/v1/kyc-sessions/{id}/documents", async (string id, IFormFile file, DocumentType type, IKycVerificationService kyc, CancellationToken ct) =>
{
    await using var stream = file.OpenReadStream();
    var document = await kyc.SubmitDocumentAsync(new SubmitDocumentRequest
    {
        SessionId = id,
        Type = type,
        Image = stream,
        FileName = file.FileName,
        ContentType = file.ContentType,
    }, ct);
    return Results.Ok(new { document.StorageKey, document.ContentType, document.SizeInBytes });
}).DisableAntiforgery();

// Current status.
app.MapGet("/api/v1/kyc-sessions/{id}", async (string id, IKycVerificationService kyc, CancellationToken ct) =>
{
    var result = await kyc.GetVerificationStatusAsync(id, ct);
    return Results.Ok(new { status = result.Status.ToString(), result.RejectionCode, result.RejectionReason });
});

// Identomat webhook: verify signature, dedupe (at-least-once), fetch + record the result.
app.MapPost("/api/v1/kyc-webhooks/identomat", async (HttpRequest request, IKycWebhookProcessor processor, IOptions<IdentomatOptions> options, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var rawBody = await reader.ReadToEndAsync(ct);
    var signature = request.Headers[options.Value.WebhookSignatureHeader].FirstOrDefault();

    try
    {
        var outcome = await processor.ProcessAsync(new KycWebhookRequest { RawBody = rawBody, Signature = signature }, ct);
        return Results.Ok(new { outcome.Handled, outcome.Duplicate, outcome.ProviderSessionId, status = outcome.Status?.ToString() });
    }
    catch (WebhookSignatureException)
    {
        return Results.Unauthorized();
    }
}).DisableAntiforgery();

await app.RunAsync();

// Exposed so the test project can spin the app up with GmWebApplicationFactory.
public partial class Program
{
    // Hoisted out of the '/' handler (CA1861): a fresh array per request is unnecessary allocation
    // for a constant, read-only payload.
    private static readonly string[] Endpoints =
    [
        "POST /api/v1/kyc-sessions                    { applicantReference } → start a session (returns widget URL)",
        "POST /api/v1/kyc-sessions/{id}/documents     multipart 'file' + 'type' → normalize + store + submit a document",
        "GET  /api/v1/kyc-sessions/{id}               → current status",
        "POST /api/v1/kyc-webhooks/identomat          Identomat callback (signature-verified, deduped)",
    ];

    // Only used as a WebApplicationFactory<Program> marker; never instantiated directly.
    protected Program() { }
}

namespace GM.KYC.Sample.API
{
    public sealed record StartSessionRequest(string ApplicantReference);
}
