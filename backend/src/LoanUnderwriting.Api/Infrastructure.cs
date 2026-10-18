using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using LoanUnderwriting.Core;
using LoanUnderwriting.Infrastructure;
using LoanUnderwriting.Infrastructure.Workflow;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace LoanUnderwriting.Api;

public static class Roles
{
    public const string RiskOps = "RiskOps", Underwriter = "Underwriter", Auditor = "Auditor", PolicyOwner = "PolicyOwner";
}

/// <summary>
/// DEVELOPMENT ONLY authentication: trusts the X-Demo-User and X-Demo-Role headers (defaults to a Risk Ops user).
/// It is only registered when Auth:Mode is "Dev". Production uses Auth:Mode "Jwt" (OIDC bearer tokens).
/// </summary>
public sealed class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevHeader";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Demo-User"].FirstOrDefault() ?? "Ethan Cole";
        var role = Request.Headers["X-Demo-Role"].FirstOrDefault() ?? Roles.RiskOps;
        var claims = new[] { new Claim(ClaimTypes.Name, user), new Claim(ClaimTypes.Role, role) };
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public static class UserExtensions
{
    public static string DisplayName(this ClaimsPrincipal p) =>
        p.FindFirstValue("name") ?? p.FindFirstValue(ClaimTypes.Name) ?? p.FindFirstValue("preferred_username") ?? "unknown";
}

public sealed class CasesHub : Hub
{
    public Task JoinCase(string caseId) => Groups.AddToGroupAsync(Context.ConnectionId, caseId);
    public Task LeaveCase(string caseId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, caseId);
}

public sealed class SignalRNotifier(IHubContext<CasesHub> hub) : IProgressNotifier
{
    public Task StepChangedAsync(Guid caseId, StepInfo step) => hub.Clients.Group(caseId.ToString()).SendAsync("StepChanged", caseId, step);
    public Task StatusChangedAsync(Guid caseId, CaseStatus status) => hub.Clients.Group(caseId.ToString()).SendAsync("StatusChanged", caseId, status.ToString());
}

public sealed class RunQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public void Enqueue(Guid caseId) => _channel.Writer.TryWrite(caseId);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Runs queued cases with a concurrency limit (protects a single-GPU model host) and recovers unfinished cases at start.</summary>
public sealed class CaseRunnerService(RunQueue queue, WorkflowRunner runner, ICaseRepository repo, WorkflowOptions options, ILogger<CaseRunnerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var id in await repo.ListByStatusAsync(CaseStatus.Processing, stoppingToken)) queue.Enqueue(id);
        var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRuns));
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            await gate.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try { await runner.RunAsync(id, stoppingToken); }
                catch (Exception ex) { log.LogError(ex, "Run failed for case {Case}", id); }
                finally { gate.Release(); }
            }, stoppingToken);
        }
    }
}
