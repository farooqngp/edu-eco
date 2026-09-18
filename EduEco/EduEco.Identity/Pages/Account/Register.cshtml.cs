using System.ComponentModel.DataAnnotations;
using System.Text;
using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using EduEco.Identity.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace EduEco.Identity.Pages.Account;

/// <summary>
/// Self-service sign-up (only when <c>IdentityServer:AllowSelfRegistration</c> is on). The account starts unconfirmed and
/// without tenant access: the user must confirm the email address, and a tenant administrator must add a membership
/// before any application can obtain a token for the user. The response is identical for new and existing addresses.
/// </summary>
[AllowAnonymous]
public sealed class RegisterModel(
    UserManager<ApplicationUser> userManager,
    IEmailSender<ApplicationUser> emailSender,
    IOptions<IdentityServerOptions> options,
    ILogger<RegisterModel> logger) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool Submitted { get; private set; }

    public IActionResult OnGet() => options.Value.AllowSelfRegistration ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!options.Value.AllowSelfRegistration)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var email = Input.Email.Trim();
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            // Same response as a new registration: the page must not reveal which addresses have accounts.
            Submitted = true;
            return Page();
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = Input.DisplayName.Trim(),
            EmailConfirmed = false,
        };

        var result = await userManager.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            // Only password policy errors reach the user; duplicate-name races are reported like a success.
            if (result.Errors.All(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
            {
                Submitted = true;
                return Page();
            }

            foreach (var error in result.Errors.Where(e => e.Code is not ("DuplicateUserName" or "DuplicateEmail")))
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = Url.Page("/Account/ConfirmEmail", pageHandler: null, values: new { userId = user.Id, code }, protocol: Request.Scheme)!;
        await emailSender.SendConfirmationLinkAsync(user, email, link);
        AuditLog.UserRegistered(logger, user.Id);

        Submitted = true;
        return Page();
    }

    public sealed class InputModel
    {
        [Required]
        [EmailAddress]
        [StringLength(256)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(100, MinimumLength = 2)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [StringLength(128, MinimumLength = 12)]
        public string Password { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        [Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
