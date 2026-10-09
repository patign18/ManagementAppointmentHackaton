using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using ManagementAppointment.Data;
using ManagementAppointment.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ManagementAppointment.Services;

// Email actions require no patient account; GET never changes an appointment.
public class AppointmentResponseService(IDbContextFactory<ClinicDbContext> factory, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("ManagementAppointment.EmailResponse.v1");
    private record Payload(int AppointmentId, int PatientId, long StartTicks, long ExpiresTicks, bool Confirm);
    public string Token(Appointment appointment, bool confirm)
    {
        var expires = confirm ? appointment.ConfirmationDueUtc ?? appointment.StartUtc : appointment.StartUtc;
        return protector.Protect(JsonSerializer.Serialize(new Payload(appointment.Id, appointment.PatientId, appointment.StartUtc.Ticks, expires.Ticks, confirm)));
    }
    private Payload Read(string token)
    {
        try
        {
            var data = JsonSerializer.Deserialize<Payload>(protector.Unprotect(token));
            if (data == null || data.ExpiresTicks <= DateTime.UtcNow.Ticks) throw new ClinicException("El enlace venció. Contacta a la oficina.");
            return data;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { throw new ClinicException("El enlace no es válido. Contacta a la oficina."); }
    }
    public string ActionLabel(string token) => Read(token).Confirm ? "Confirmar asistencia" : "Cancelar mi cita";
    public async Task<string> ApplyAsync(string token)
    {
        var data = Read(token);
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await AppointmentRecovery.LockAsync(db, $"{db.Model.GetDefaultSchema()}:recovery");
        var item = await db.Appointments.Include(x => x.Patient).Include(x => x.Doctor).SingleOrDefaultAsync(x => x.Id == data.AppointmentId && x.PatientId == data.PatientId)
            ?? throw new ClinicException("La cita no está disponible.");
        if (item.StartUtc.Ticks != data.StartTicks || item.StartUtc <= DateTime.UtcNow || item.Patient.IsDemo || !item.Patient.ContactConsent || !item.Patient.IsActive || !item.Doctor.IsActive || !await db.Users.AnyAsync(x => x.Id == item.Doctor.UserId && x.IsActive)) throw new ClinicException("La cita cambió o el enlace ya no está disponible. Contacta a la oficina.");
        if (data.Confirm && item.Status == AppointmentStatus.Confirmed) return "Tu asistencia ya está confirmada.";
        if (!data.Confirm && item.Status == AppointmentStatus.Cancelled) return "La cita ya fue cancelada.";
        if (item.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed) || (data.Confirm && item.ConfirmationDueUtc <= DateTime.UtcNow)) throw new ClinicException("El plazo venció o la cita ya se resolvió. Contacta a la oficina.");
        item.Status = data.Confirm ? AppointmentStatus.Confirmed : AppointmentStatus.Cancelled;
        db.AppointmentHistory.Add(new() { AppointmentId = item.Id, ActorId = "respuesta-correo", Action = data.Confirm ? "Asistencia confirmada desde el enlace del correo" : "Paciente canceló desde el enlace del correo; espacio disponible para reasignación automática" });
        AppointmentRecovery.Notice(db, item.Doctor.UserId, $"Respuesta del paciente: cita #{item.Id} {(data.Confirm ? "confirmada" : "cancelada")} desde el correo.");
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return data.Confirm ? "Tu asistencia quedó confirmada. La oficina conserva tu cita." : "Tu cita quedó cancelada. La oficina reasignará el espacio automáticamente.";
    }
}
