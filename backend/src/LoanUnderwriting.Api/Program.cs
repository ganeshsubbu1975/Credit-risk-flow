using System.Security.Claims;
using LoanUnderwriting.Api;
using LoanUnderwriting.Core;
using LoanUnderwriting.Infrastructure;
using LoanUnderwriting.Infrastructure.Sql;
using LoanUnderwriting.Infrastructure.Workflow;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // As a Windows Service the working directory is System32, so anchor content and config to the install folder.
    ContentRootPath = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService();
var cfg = builder.Configuration;

builder.Services.AddLoanUnderwriting(cfg);
builder.Services.AddSingleton<IProgressNotifier, SignalRNotifier>();
builder.Services.AddSingleton<RunQueue>();
builder.Services.AddHostedService<CaseRunnerService>();
builder.Services.AddSignalR();
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(cfg.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:5173" })
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var authMode = cfg["Auth:Mode"] ?? "Jwt";
if (authMode.Equals("Dev", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName).AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        o.Authority = cfg["Auth:Authority"];
        o.Audience = cfg["Auth:Audience"];
        o.TokenValidationParameters.RoleClaimType = cfg["Auth:RoleClaim"] ?? "roles";
        o.TokenValidationParameters.NameClaimType = "name";
        o.Events = new JwtBearerEvents
        {
            // SignalR sends the token in the query string for WebSockets.
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
                return Task.CompletedTask;
            },
        };
    });
}
builder.Services.AddAuthorization();

var app = builder.Build();
var log = app.Logger;
if (authMode.Equals("Dev", StringComparison.OrdinalIgnoreCase))
    log.LogWarning("Auth:Mode is Dev: identity headers are trusted. Do not use outside development.");

// Startup: create/verify the schema, then the self-check. A failed check stops the application (PRD FR-11).
IReadOnlyList<CheckResult> startupChecks = Array.Empty<CheckResult>();
{
    var db = app.Services.GetRequiredService<DatabaseOptions>();
    if (db.AutoCreateSchema) await app.Services.GetRequiredService<SchemaInitializer>().EnsureAsync();
    var sc = app.Services.GetRequiredService<SelfCheckOptions>();
    if (sc.Enabled)
    {
        startupChecks = await app.Services.GetRequiredService<SelfCheck>().RunAsync();
        if (startupChecks.Any(c => !c.Ok)) throw new InvalidOperationException("Startup self-check failed: " + string.Join("; ", startupChecks.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}")));
    }
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");
var all = new[] { Roles.RiskOps, Roles.Underwriter, Roles.Auditor };

api.MapGet("/health", (DeploymentOptions d) => Results.Ok(new { status = "ok", mode = d.Mode.ToString(), checks = startupChecks.Select(c => new { c.Name, c.Ok }) })).AllowAnonymous();

api.MapPost("/cases", async (HttpRequest req, ClaimsPrincipal user, IDocumentStore store, IPdfReader pdf, ICaseRepository repo, RunQueue queue, StorageOptions storage, CancellationToken ct) =>
{
    if (!req.HasFormContentType) return Results.Problem("Send the PDF as multipart/form-data in a field named 'file'.", statusCode: 400);
    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.Problem("No file was uploaded.", statusCode: 400);
    if (file.Length > storage.MaxUploadMb * 1024L * 1024L) return Results.Problem($"The file is larger than {storage.MaxUploadMb} MB.", statusCode: 413);

    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    var bytes = ms.ToArray();
    if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F') return Results.Problem("Only PDF files are accepted.", statusCode: 400);
    try
    {
        if (!pdf.Read(new MemoryStream(bytes)).HasText) return Results.Problem("The PDF has no text layer. Scanned or image-only PDFs are not supported.", statusCode: 422);
    }
    catch (Exception) { return Results.Problem("The PDF could not be read.", statusCode: 422); }

    var doc = await store.SaveAsync(new MemoryStream(bytes), file.FileName, ct);
    var id = Guid.NewGuid();
    var state = new LoanState
    {
        CaseId = id, EntityName = Path.GetFileNameWithoutExtension(file.FileName), FileName = doc.FileName, SubmittedBy = user.DisplayName(),
        SubmittedAt = DateTimeOffset.UtcNow, Document = doc, Status = CaseStatus.Processing, Steps = WorkflowRunner.NewSteps(),
    };
    await repo.CreateAsync(state, ct);
    await repo.AppendAuditAsync(id, new AuditEvent(DateTimeOffset.UtcNow, state.SubmittedBy, "Upload", $"File {doc.FileName} accepted ({bytes.Length / 1024} KB)"), ct);
    queue.Enqueue(id);
    return Results.Accepted($"/api/cases/{id}", new { caseId = id });
}).RequireAuthorization(p => p.RequireRole(Roles.RiskOps)).DisableAntiforgery();

api.MapGet("/cases", async (ICaseRepository repo, CancellationToken ct) => Results.Ok(await repo.ListAsync(ct)))
   .RequireAuthorization(p => p.RequireRole(all));

api.MapGet("/cases/{id:guid}", async (Guid id, ICaseRepository repo, CancellationToken ct) =>
    await repo.LoadAsync(id, ct) is { } s ? Results.Ok(s) : Results.NotFound())
   .RequireAuthorization(p => p.RequireRole(all));

api.MapPost("/cases/{id:guid}/review", async (Guid id, ReviewRequest body, ClaimsPrincipal user, ICaseRepository repo, WorkflowRunner runner, CancellationToken ct) =>
{
    var state = await repo.LoadAsync(id, ct);
    if (state is null) return Results.NotFound();
    var me = user.DisplayName();
    if (state.SubmittedBy.Equals(me, StringComparison.OrdinalIgnoreCase))
        return Results.Problem("Separation of duties: the person who submitted a case cannot decide it.", statusCode: 403);
    if (state.Status != CaseStatus.AwaitingReview) return Results.Problem("The case is not waiting for review.", statusCode: 409);

    Outcome final;
    switch (body.Action)
    {
        case "Approve": final = Outcome.Approve; break;
        case "Deny": final = Outcome.Deny; break;
        case "Override":
            if (body.FinalDecision is not (Outcome.Approve or Outcome.Deny)) return Results.Problem("An override must state finalDecision Approve or Deny.", statusCode: 400);
            if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < 5) return Results.Problem("An override needs a reason of at least 5 characters.", statusCode: 400);
            final = body.FinalDecision.Value;
            break;
        default: return Results.Problem("Action must be Approve, Deny or Override.", statusCode: 400);
    }
    await runner.ResumeAsync(id, new HumanDecision(body.Action, final, body.Reason?.Trim() ?? "", me, DateTimeOffset.UtcNow), ct);
    return Results.Ok(await repo.LoadAsync(id, ct));
}).RequireAuthorization(p => p.RequireRole(Roles.Underwriter));

api.MapGet("/cases/{id:guid}/record", async (Guid id, string? view, ClaimsPrincipal user, ICaseRepository repo, CancellationToken ct) =>
{
    var s = await repo.LoadAsync(id, ct);
    if (s is null) return Results.NotFound();
    if (s.Final is null) return Results.Problem("The case has no final decision yet.", statusCode: 409);
    var applicant = new
    {
        caseId = s.CaseId, entity = s.EntityName, decision = s.Final == Outcome.Approve ? "Approved" : "Declined",
        decidedAt = s.FinalAt,
        reasons = s.Final == Outcome.Deny ? s.Decision?.ReasonCodes : null,
    };
    if (!"internal".Equals(view, StringComparison.OrdinalIgnoreCase)) return Results.Ok(applicant);
    if (!user.IsInRole(Roles.Underwriter) && !user.IsInRole(Roles.Auditor)) return Results.Forbid();
    return Results.Ok(new { applicant, riskScore = s.Scorecard?.RiskScore, flags = s.Flags, decisionModel = s.Decision, human = s.Human, aiRecommendation = s.LlmRecommendation, audit = await repo.GetAuditAsync(id, ct) });
}).RequireAuthorization(p => p.RequireRole(all));

api.MapPost("/cases/{id:guid}/qa", async (Guid id, QaRequest body, ClaimsPrincipal user, ICaseRepository repo, QaService qa, CancellationToken ct) =>
{
    var s = await repo.LoadAsync(id, ct);
    if (s is null) return Results.NotFound();
    if (s.ChunkCount == 0) return Results.Problem("The document is still being loaded.", statusCode: 409);
    var r = await qa.AskAsync(id, s.EntityName, user.DisplayName(), body.Question ?? "", ct);
    return Results.Ok(new { answer = r.Answer, citations = r.Citations, refused = r.Refused, reason = r.Refused ? r.Reason.ToString() : null });
}).RequireAuthorization(p => p.RequireRole(Roles.RiskOps, Roles.Underwriter));

api.MapGet("/cases/{id:guid}/qa", async (Guid id, ICaseRepository repo, CancellationToken ct) => Results.Ok(await repo.GetQaLogAsync(id, ct)))
   .RequireAuthorization(p => p.RequireRole(all));

api.MapGet("/cases/{id:guid}/audit", async (Guid id, ICaseRepository repo, CancellationToken ct) => Results.Ok(await repo.GetAuditAsync(id, ct)))
   .RequireAuthorization(p => p.RequireRole(Roles.Underwriter, Roles.Auditor));

api.MapGet("/policy", async (ICaseRepository repo, CancellationToken ct) => Results.Ok(await repo.GetActivePolicyAsync(ct)))
   .RequireAuthorization(p => p.RequireRole(all));

app.MapHub<CasesHub>("/hubs/cases").RequireAuthorization();
app.MapFallbackToFile("index.html");
app.Run();

public sealed record ReviewRequest(string Action, Outcome? FinalDecision, string? Reason);
public sealed record QaRequest(string? Question);

public partial class Program { }
