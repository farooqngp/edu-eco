using System.ComponentModel.DataAnnotations;
using EduEco.Api.Security;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Abstractions.Security;
using EduEco.Application.Invites;
using EduEco.Core.Authorization;
using EduEco.Infrastructure.Persistence;
using EduEco.Infrastructure.Persistence.Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EduEco.Api.Controllers;

public sealed record RegisterRequest(
    [Required, StringLength(64)] string InviteCode,
    [Required, EmailAddress, StringLength(256)] string Email,
    [Phone, StringLength(32)] string? PhoneNumber,
    [Required, StringLength(100, MinimumLength = 2)] string DisplayName,
    [Required, StringLength(128, MinimumLength = 12)] string Password);

public sealed record RegisterResponse(bool RequiresEmailConfirmation);

public sealed record LoginRequest([Required] string Identifier, [Required] string Password);

public sealed record RefreshRequest([Required] string RefreshToken);

public sealed record ForgotPasswordRequest([Required, EmailAddress] string Email);

public sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn, string TokenType);

/// <summary>
/// Angular-facing gateway: register/login/refresh/forgot-password go through EduEco.Api, which talks to Identity
/// server-to-server (<see cref="IdentityInternalClient"/>) instead of the browser redirecting there directly.
/// All anonymous — there is no cookie session on this API either way, so CSRF does not apply.
/// </summary>
[Route("api/v1/auth")]
[Authorize]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class AuthController(
    IdentityInternalClient identityClient,
    InviteService inviteService,
    IInviteQueries inviteQueries,
    IDbSession dbSession,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IOptions<DatabaseOptions> databaseOptions) : ApiControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<RegisterResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RegisterResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var redemption = await inviteService.ValidateAsync(request.InviteCode, cancellationToken);
        if (!redemption.Succeeded)
        {
            return ProblemFor(redemption);
        }

        var registration = await identityClient.RegisterAsync(request.Email, request.PhoneNumber, request.DisplayName, request.Password, cancellationToken)
            .ConfigureAwait(false);

        if (registration.Outcome == RegistrationOutcome.ValidationFailed)
        {
            return Problem(detail: string.Join(' ', registration.Errors), statusCode: StatusCodes.Status400BadRequest);
        }

        if (registration.Outcome == RegistrationOutcome.Created)
        {
            await AttachMembershipAsync(redemption.Value!, registration.UserId!.Value, cancellationToken).ConfigureAwait(false);
        }

        // Same response whether the account was just created or already existed (no email enumeration).
        return Accepted(new RegisterResponse(RequiresEmailConfirmation: true));
    }

    [HttpPost("login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await identityClient.PasswordSignInAsync(request.Identifier, request.Password, cancellationToken).ConfigureAwait(false);
        return token is null
            ? Problem(detail: "The username or password is incorrect.", statusCode: StatusCodes.Status400BadRequest)
            : new TokenResponse(token.AccessToken, token.RefreshToken, token.ExpiresIn, token.TokenType);
    }

    [HttpPost("refresh")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await identityClient.RefreshAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);
        return token is null
            ? Problem(detail: "The refresh token is invalid or expired.", statusCode: StatusCodes.Status400BadRequest)
            : new TokenResponse(token.AccessToken, token.RefreshToken, token.ExpiresIn, token.TokenType);
    }

    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await identityClient.RequestPasswordResetAsync(request.Email, cancellationToken).ConfigureAwait(false);
        return Accepted();
    }

    /// <summary>
    /// Creates the tenant membership and marks the invite consumed, in one transaction. The normal request-scoped
    /// <see cref="ITenantContext"/> has no tenant (anonymous request) and would reject this write, so both repositories
    /// are built here against a context fixed to the redeemed invite's tenant instead of the DI-registered one.
    /// </summary>
    private async Task AttachMembershipAsync(InviteRedemption redemption, long userId, CancellationToken cancellationToken)
    {
        var tenantContext = new FixedTenantContext(redemption.TenantId);
        var memberships = new DapperCommandRepository<UserTenantMembership>(dbSession, tenantContext, currentUser, timeProvider, databaseOptions);
        var invites = new InviteService(
            new DapperCommandRepository<TenantInvite>(dbSession, tenantContext, currentUser, timeProvider, databaseOptions),
            inviteQueries,
            timeProvider);

        var unitOfWork = (IUnitOfWork)dbSession;
        await unitOfWork.BeginAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await memberships.InsertAsync(
                new UserTenantMembership { UserId = userId, RoleId = Roles.All.Single(r => r.Name == redemption.RoleName).Id },
                cancellationToken).ConfigureAwait(false);

            var consumed = await invites.ConsumeAsync(redemption.InviteId, userId, cancellationToken).ConfigureAwait(false);
            if (!consumed.Succeeded)
            {
                // Someone else redeemed the same single-use code in the meantime; the membership insert above still rolls back.
                throw new InvalidOperationException($"Invite {redemption.InviteId} could not be consumed: {consumed.Detail}");
            }

            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private sealed class FixedTenantContext(long tenantId) : ITenantContext
    {
        public long? TenantId { get; } = tenantId;

        public bool IsCrossTenant => false;
    }
}
