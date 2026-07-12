using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ResearchDiscovery.Api.Auth;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>
/// Account administration: look up users, adjust roles, grant budget, manage
/// invite codes. Every mutation writes an AdminActionLog row — privileged
/// operations leave a trail.
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = AuthPolicies.Admin)]
public class AdminUsersController(AppDbContext db) : ControllerBase
{
    public sealed record UserView(
        long Id,
        string Email,
        string DisplayName,
        string Role,
        bool IsActive,
        DateTimeOffset CreatedUtc,
        DateTimeOffset LastSeenUtc,
        long GrantedMicros,
        long SpentMicros);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? query, CancellationToken ct)
    {
        var users = db.AppUsers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = $"%{query.Trim()}%";
            users = users.Where(u =>
                EF.Functions.Like(u.Email, term) || EF.Functions.Like(u.DisplayName, term));
        }

        var views = await users
            .OrderByDescending(u => u.LastSeenUtc)
            .Take(100)
            .Select(u => new UserView(
                u.Id, u.Email, u.DisplayName, u.Role.ToString(), u.IsActive,
                u.CreatedUtc, u.LastSeenUtc,
                db.BudgetGrants.Where(g => g.UserId == u.Id)
                    .Sum(g => (long?)g.AmountMicros) ?? 0,
                db.LlmUsageEvents.Where(e => e.UserId == u.Id && !e.UsedByoKey)
                    .Sum(e => (long?)e.CostMicros) ?? 0))
            .ToListAsync(ct);

        return Ok(views);
    }

    public sealed record GrantRequest(long AmountMicros, string? Note);

    [HttpPost("{id:long}/grants")]
    public async Task<IActionResult> Grant(
        long id, [FromBody] GrantRequest request, CancellationToken ct)
    {
        if (request.AmountMicros is <= 0 or > 1_000_000_000)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "amountMicros must be between 1 and 1,000,000,000 ($1000).");
        }

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, detail: "No such user.");
        }

        var actor = HttpContext.GetAppUser()!;
        var now = DateTimeOffset.UtcNow;
        db.BudgetGrants.Add(new BudgetGrant
        {
            UserId = user.Id,
            AmountMicros = request.AmountMicros,
            Reason = "admin",
            GrantedByUserId = actor.Id,
            CreatedUtc = now,
        });
        db.AdminActionLogs.Add(new AdminActionLog
        {
            ActorUserId = actor.Id,
            Action = "budget.grant",
            TargetUserId = user.Id,
            Detail = $"{{\"amountMicros\":{request.AmountMicros},\"note\":\"{request.Note ?? ""}\"}}",
            CreatedUtc = now,
        });
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    public sealed record RoleRequest(string Role);

    [HttpPut("{id:long}/role")]
    public async Task<IActionResult> SetRole(
        long id, [FromBody] RoleRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "role must be Member or Admin.");
        }

        var actor = HttpContext.GetAppUser()!;
        if (actor.Id == id && role != UserRole.Admin)
        {
            // Self-demotion is how you lock yourself out of the admin surface.
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "You cannot remove your own admin role.");
        }

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, detail: "No such user.");
        }

        var previous = user.Role;
        user.Role = role;
        // Promotion implies activation: an admin stuck behind the invite gate
        // makes no sense.
        if (role == UserRole.Admin)
        {
            user.IsActive = true;
        }

        db.AdminActionLogs.Add(new AdminActionLog
        {
            ActorUserId = actor.Id,
            Action = "user.role.set",
            TargetUserId = user.Id,
            Detail = $"{{\"from\":\"{previous}\",\"to\":\"{role}\"}}",
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    public sealed record InviteView(
        long Id, string Code, int MaxUses, int UsedCount, DateTimeOffset? ExpiresUtc,
        DateTimeOffset CreatedUtc);

    [HttpGet("/api/admin/invites")]
    public async Task<IActionResult> ListInvites(CancellationToken ct)
    {
        var invites = await db.InviteCodes.AsNoTracking()
            .OrderByDescending(c => c.CreatedUtc)
            .Take(100)
            .Select(c => new InviteView(
                c.Id, c.Code, c.MaxUses, c.UsedCount, c.ExpiresUtc, c.CreatedUtc))
            .ToListAsync(ct);
        return Ok(invites);
    }

    public sealed record CreateInviteRequest(int? MaxUses, int? ExpiresDays);

    [HttpPost("/api/admin/invites")]
    public async Task<IActionResult> CreateInvite(
        [FromBody] CreateInviteRequest request, CancellationToken ct)
    {
        if (request.MaxUses is < 1 or > 1000)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "maxUses must be between 1 and 1000.");
        }

        var actor = HttpContext.GetAppUser()!;
        var now = DateTimeOffset.UtcNow;
        var invite = new InviteCode
        {
            Code = GenerateCode(),
            MaxUses = request.MaxUses ?? 1,
            ExpiresUtc = request.ExpiresDays is { } days and > 0 ? now.AddDays(days) : null,
            CreatedByUserId = actor.Id,
            CreatedUtc = now,
        };
        db.InviteCodes.Add(invite);
        db.AdminActionLogs.Add(new AdminActionLog
        {
            ActorUserId = actor.Id,
            Action = "invite.create",
            Detail = $"{{\"code\":\"{invite.Code}\",\"maxUses\":{invite.MaxUses}}}",
            CreatedUtc = now,
        });
        await db.SaveChangesAsync(ct);

        return Ok(new InviteView(
            invite.Id, invite.Code, invite.MaxUses, invite.UsedCount, invite.ExpiresUtc,
            invite.CreatedUtc));
    }

    /// <summary>
    /// Human-friendly code: unambiguous alphabet (no 0/O/1/I), FW- prefix so
    /// codes are recognizably Fatwood's.
    /// </summary>
    private static string GenerateCode()
    {
        const string alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return $"FW-{new string(chars)}";
    }
}
