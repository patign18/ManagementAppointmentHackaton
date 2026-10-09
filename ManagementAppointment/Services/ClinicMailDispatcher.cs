using System.Net;
using System.Net.Mail;
using ManagementAppointment.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;

namespace ManagementAppointment.Services;

public class ClinicMailDispatcher(IDbContextFactory<ClinicDbContext> factory, IConfiguration config, AppointmentResponseService responses, ILogger<ClinicMailDispatcher> logger)
{
    public async Task DispatchAsync(CancellationToken token)
    {
        if (!config.GetValue<bool>("Mail:Enabled")) return;
        await using var db = await factory.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await AppointmentRecovery.LockAsync(db, $"{db.Model.GetDefaultSchema()}:mail");
        var before = DateTime.UtcNow.AddMinutes(-5);
        var rows = await db.ClinicMails.Where(x => x.SentUtc == null && !x.Suppressed && x.Attempts < 5 && (x.LastAttemptUtc == null || x.LastAttemptUtc < before)).OrderBy(x => x.Id).Take(10).ToListAsync(token);
        foreach (var row in rows)
        {
            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == row.UserId && x.IsActive, token);
            var patient = row.PatientId.HasValue ? await db.Patients.FindAsync([row.PatientId.Value], token) : null;
            var recipient = patient?.Email ?? user?.Email;
            var allowed = patient != null ? patient.IsActive && !patient.IsDemo && patient.ContactConsent : user is { IsActive: true, EmailConsent: true };
            row.LastAttemptUtc = DateTime.UtcNow; row.Attempts++;
            if (!allowed || string.IsNullOrWhiteSpace(recipient) || row.CreatedUtc < DateTime.UtcNow.AddHours(-1)) { row.Suppressed = true; continue; }
            try
            {
                var body = row.Message;
                if (row.IncludeResponseLinks && row.AppointmentId.HasValue)
                {
                    var appointment = await db.Appointments.FindAsync([row.AppointmentId.Value], token);
                    if (appointment != null && row.AppointmentStartUtc.HasValue && appointment.StartUtc != row.AppointmentStartUtc) { row.Suppressed = true; continue; }
                    if (appointment == null || appointment.StartUtc <= DateTime.UtcNow || appointment.Status is not (Models.AppointmentStatus.Pending or Models.AppointmentStatus.Confirmed)) { row.Suppressed = true; continue; }
                    var baseUrl = (config["Office:PublicUrl"] ?? "https://localhost:7246").TrimEnd('/') + "/respuesta-cita";
                    if (appointment.Status == Models.AppointmentStatus.Pending && (appointment.ConfirmationDueUtc == null || appointment.ConfirmationDueUtc > DateTime.UtcNow)) body += "\n\nConfirmar asistencia: " + QueryHelpers.AddQueryString(baseUrl, "token", responses.Token(appointment, true));
                    body += "\n\nCancelar cita: " + QueryHelpers.AddQueryString(baseUrl, "token", responses.Token(appointment, false));
                }
                using var client = new SmtpClient(config["Mail:Host"], config.GetValue("Mail:Port", 587)) { EnableSsl = true, Credentials = new NetworkCredential(config["Mail:Username"], config["Mail:Password"]) };
                using var message = new MailMessage(config["Mail:From"]!, recipient) { Subject = "Actualización de tu cita · ManagementAppointment", Body = body + "\n\nOficina ManagementAppointment. No es necesario crear una cuenta." };
                await client.SendMailAsync(message, token); row.SentUtc = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is SmtpException or ArgumentException or InvalidOperationException) { logger.LogWarning("No se pudo entregar un aviso de agenda. Revisa la configuración SMTP."); }
        }
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
    }
}
