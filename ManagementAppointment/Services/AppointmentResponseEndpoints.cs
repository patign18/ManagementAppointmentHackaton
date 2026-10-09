using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace ManagementAppointment.Services;

public static class AppointmentResponseEndpoints
{
    public static void MapAppointmentResponseEndpoints(this WebApplication app)
    {
        app.MapGet("/respuesta-cita", (HttpContext context, string token, AppointmentResponseService responses, IAntiforgery antiforgery) =>
        {
            try
            {
                var label = responses.ActionLabel(token); var csrf = antiforgery.GetAndStoreTokens(context);
                var html = "<!doctype html><html lang='es'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Respuesta a la oficina</title><body style='font-family:system-ui;max-width:540px;margin:60px auto;padding:24px'><h1>ManagementAppointment</h1><p>Envía tu respuesta a la oficina. No necesitas registrarte ni iniciar sesión.</p><form method='post' action='/respuesta-cita'><input type='hidden' name='token' value='" + HtmlEncoder.Default.Encode(token) + "'><input type='hidden' name='" + HtmlEncoder.Default.Encode(csrf.FormFieldName) + "' value='" + HtmlEncoder.Default.Encode(csrf.RequestToken!) + "'><button style='padding:14px;background:#069b91;color:white;border:0;border-radius:8px' type='submit'>" + label + "</button></form></body></html>";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                return Results.Content(html, "text/html");
            }
            catch (ClinicException ex) { return Results.BadRequest(ex.Message); }
        }).RequireRateLimiting("accounts");
        app.MapPost("/respuesta-cita", async (HttpContext context, AppointmentResponseService responses, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Text(await responses.ApplyAsync(form["token"].ToString()));
            }
            catch (AntiforgeryValidationException) { return Results.BadRequest("La sesión venció. Abre de nuevo el enlace del correo."); }
            catch (ClinicException ex) { return Results.BadRequest(ex.Message); }
        }).RequireRateLimiting("accounts");
    }
}
