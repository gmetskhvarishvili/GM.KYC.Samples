# GM.KYC.Samples

Runnable usage for [GM.KYC](https://github.com/gmetskhvarishvili/GM.KYC) — Identomat-backed KYC
verification for the `GM.*` ecosystem.

`GM.KYC.Sample.API` wires the whole stack — `AddGMKyc().AddIdentomat().AddKycPersistence(...)` plus the
infra it needs (GM.Idempotency for webhook dedup, GM.FileStorage.Local, a config-backed
`ISecretsService`) — and exposes the KYC flow:

```
POST /kyc/sessions                 { applicantReference }        → start a session (returns the widget URL)
POST /kyc/sessions/{id}/documents  multipart 'file' + 'type'     → normalize + store + submit a document
GET  /kyc/sessions/{id}                                          → current status
POST /kyc/webhooks/identomat       Identomat callback            → signature-verified, deduped, recorded
```

```bash
dotnet run --project GM.KYC.Sample.API
```

> Set the Identomat `companyKey` via configuration/env for the demo secrets service, e.g.
> `Secrets__Identomat__CompanyKey=...`. In production use a real GM.Secrets provider.

## Tests

`dotnet test` drives the API through GM.Testing's `GmWebApplicationFactory` with a **fake
`IKycProvider`** swapped in — so the flow (start session → status) and the webhook endpoint are verified
end to end without calling Identomat. It's a nice demonstration of the GM.Testing kit (published earlier)
testing a GM.* service.

> The projects reference the sibling `GM.KYC` source repo by project path. Once the KYC packages are
> published, swap the `ProjectReference`s for `PackageReference`s.
