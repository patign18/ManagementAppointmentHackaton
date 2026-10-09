using System.ComponentModel.DataAnnotations;
using System.Text;
using ManagementAppointment.Data;
using ManagementAppointment.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace ManagementAppointment.Services;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/auth").RequireRateLimiting("accounts");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("La sesión del formulario venció. Recarga la página y vuelve a intentarlo."); }
            return await next(context);
        });

        group.MapPost("/login", async (HttpContext context, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) =>
        {
            var form = await context.Request.ReadFormAsync(); var email = form["email"].ToString().Trim();
            var user = await users.FindByEmailAsync(email);
            if (user is null || !user.IsActive || !(await users.GetRolesAsync(user)).Any(r => r is Roles.Admin or Roles.Reception or Roles.Doctor)) return Results.LocalRedirect("/login?error=credentials");
            var result = await signIn.PasswordSignInAsync(user, form["password"].ToString(), form["remember"] == "true", lockoutOnFailure: true);
            if (!result.Succeeded) return Results.LocalRedirect("/login?error=credentials");
            return Results.LocalRedirect("/dashboard");
        });

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signIn) => { await signIn.SignOutAsync(); return Results.LocalRedirect("/login"); }).RequireAuthorization("ClinicStaff");

        group.MapPost("/password", async (HttpContext context, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) =>
        {
            var form = await context.Request.ReadFormAsync(); var user = await users.GetUserAsync(context.User);
            if (user is null || !user.IsActive || form["password"] != form["confirm"]) return Results.LocalRedirect("/perfil?password=error");
            var result = await users.ChangePasswordAsync(user, form["current"].ToString(), form["password"].ToString());
            if (result.Succeeded) await signIn.RefreshSignInAsync(user);
            return Results.LocalRedirect(result.Succeeded ? "/perfil?password=ok" : "/perfil?password=error");
        }).RequireAuthorization("ClinicStaff");

        group.MapPost("/forgot", async (HttpContext context, UserManager<ApplicationUser> users, AccountMailer mailer, IConfiguration configuration) =>
        {
            var form = await context.Request.ReadFormAsync(); var user = await users.FindByEmailAsync(form["email"].ToString().Trim());
            if (user is { IsActive: true } && mailer.Enabled && (await users.GetRolesAsync(user)).Any(r => r is Roles.Admin or Roles.Doctor or Roles.Reception))
            {
                var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
                var baseUrl = configuration["Office:PublicUrl"]?.TrimEnd('/') ?? "https://localhost:7246";
                var link = QueryHelpers.AddQueryString(baseUrl + "/restablecer", new Dictionary<string, string?> { ["email"] = user.Email, ["token"] = token });
                await mailer.SendRecoveryAsync(user.Email!, link);
            }
            return Results.LocalRedirect("/recuperar?sent=true");
        });

        group.MapPost("/reset", async (HttpContext context, UserManager<ApplicationUser> users) =>
        {
            var form = await context.Request.ReadFormAsync(); var user = await users.FindByEmailAsync(form["email"].ToString());
            if (user is null || !user.IsActive || form["password"] != form["confirm"] || !(await users.GetRolesAsync(user)).Any(r => r is Roles.Admin or Roles.Doctor or Roles.Reception)) return Results.LocalRedirect("/restablecer?error=invalid");
            string token;
            try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(form["token"].ToString())); }
            catch (FormatException) { return Results.LocalRedirect("/restablecer?error=invalid"); }
            var result = await users.ResetPasswordAsync(user, token, form["password"].ToString());
            if (result.Succeeded) { await users.SetLockoutEndDateAsync(user, null); await users.ResetAccessFailedCountAsync(user); }
            return Results.LocalRedirect(result.Succeeded ? "/login?reset=true" : "/restablecer?error=invalid");
        });
    }
}

