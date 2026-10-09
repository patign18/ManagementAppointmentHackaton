using System.Data;
using ManagementAppointment.Data;
using ManagementAppointment.Models;
using Microsoft.EntityFrameworkCore;

namespace ManagementAppointment.Services;

public partial class ClinicService
{
    private static void ManageAgenda(ClinicActor actor, int doctorId)
    {
        if (actor.Role != Roles.Admin && !(actor.Role == Roles.Doctor && actor.DoctorId == doctorId))
            throw new UnauthorizedAccessException("Solo el médico de esta agenda o administración puede configurarla.");
    }

    public async Task SaveRecoveryPolicyAsync(int doctorId, bool enabled, int confirmationHours, int graceMinutes, int bookingMinutes, int minimumMinutes)
    {
        var a = await ActorAsync(); ManageAgenda(a, doctorId);
        if (confirmationHours is < 1 or > 168 || graceMinutes is < 1 or > 60 || bookingMinutes is < 1 or > 120 || minimumMinutes is < 10 or > 180)
            throw new ClinicException("Usa 1–168 horas para confirmar, 1–60 minutos de tolerancia, 1–120 minutos para reservas de último momento y al menos 10 minutos de atención.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await LockAsync(db, $"{db.Model.GetDefaultSchema()}:recovery");
        var d = await db.Doctors.FindAsync(doctorId) ?? throw new ClinicException("Médico no encontrado.");
        if (minimumMinutes > d.ConsultationMinutes) throw new ClinicException("La atención mínima no puede superar la duración normal de consulta.");
        confirmationHours = 48; d.AutomaticRecovery = enabled; d.ConfirmationHours = confirmationHours; d.ArrivalGraceMinutes = graceMinutes;
        d.LastMinuteConfirmationMinutes = bookingMinutes; d.MinimumRecoveredMinutes = minimumMinutes;
        var now = DateTime.UtcNow;
        var pending = await db.Appointments.Include(x => x.Patient).Where(x => x.DoctorId == doctorId && x.StartUtc > now && x.Status == AppointmentStatus.Pending).ToListAsync();
        foreach (var item in pending)
        {
            item.ConfirmationDueUtc = enabled ? AppointmentRecovery.ConfirmationDeadline(item.StartUtc, now, confirmationHours, bookingMinutes) : null;
            item.ReminderQueuedUtc = null; item.ConfirmationReminderStage = 0;
            if (enabled) AppointmentRecovery.PatientNotice(db, item.Patient, $"Confirma la cita #{item.Id} antes de {clock.Local(item.ConfirmationDueUtc!.Value):dd/MM h.mm tt}. Después se liberará el espacio.", item.Id, true);
        }
        Audit(db, a, $"Reglas de recuperación de agenda #{doctorId}: {(enabled ? "activadas" : "pausadas")}");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task<List<WaitlistEntry>> WaitlistAsync()
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        var q = db.WaitlistEntries.AsNoTracking().Include(x => x.Doctor).Include(x => x.Patient).Include(x => x.AppointmentToAdvance).AsQueryable();
        if (a.Role == Roles.Patient) q = q.Where(x => x.PatientId == a.PatientId);
        else if (a.Role == Roles.Doctor) q = q.Where(x => x.DoctorId == a.DoctorId);
        else Staff(a);
        return await q.OrderByDescending(x => x.CreatedUtc).Take(200).ToListAsync();
    }

    public async Task<List<Appointment>> RecoveredAppointmentsAsync()
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        return await Scope(db.Appointments, a).AsNoTracking().Include(x => x.Patient).Include(x => x.Doctor).ThenInclude(x => x.Specialty)
            .Where(x => db.SlotAssignments.Any(s => s.AppointmentId == x.Id)).OrderByDescending(x => x.StartUtc).Take(100).ToListAsync();
    }

    public async Task JoinWaitlistAsync(int patientId, int doctorId, DateTime localFrom, DateTime localUntil, int minimumNoticeMinutes, int? appointmentId = null)
    {
        var a = await ActorAsync(); if (a.Role == Roles.Doctor) ManageAgenda(a, doctorId); else Staff(a);
        var from = clock.Utc(localFrom); var until = clock.Utc(localUntil); var now = DateTime.UtcNow;
        if (from < now || until <= from || until > now.AddDays(180) || minimumNoticeMinutes is < 0 or > 10080)
            throw new ClinicException("Indica un periodo futuro de hasta seis meses y un aviso de entre 0 minutos y 7 días.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await LockAsync(db, $"{db.Model.GetDefaultSchema()}:recovery");
        var patient = await db.Patients.SingleOrDefaultAsync(x => x.Id == patientId && x.IsActive) ?? throw new ClinicException("Paciente no disponible.");
        if (a.Role == Roles.Doctor && patient.RegisteredByDoctorId != a.DoctorId && !await db.Appointments.AnyAsync(x => x.PatientId == patientId && x.DoctorId == a.DoctorId)) throw new UnauthorizedAccessException();
        if (!patient.AutomaticReassignmentConsent) throw new ClinicException("Registra primero la autorización del paciente para adelantar o asignar citas automáticamente.");
        if (!patient.IsDemo && (!patient.ContactConsent || string.IsNullOrWhiteSpace(patient.Email))) throw new ClinicException("Registra el correo y la autorización de contacto del paciente para poder enviarle los cambios.");
        if (!await db.Doctors.AnyAsync(x => x.Id == doctorId && x.IsActive && x.User.IsActive && x.AutomaticRecovery)) throw new ClinicException("Este médico todavía no activó la lista de espera.");
        if (await db.WaitlistEntries.AnyAsync(x => x.PatientId == patientId && x.DoctorId == doctorId && x.Status == WaitlistStatus.Active)) throw new ClinicException("Este paciente ya está en hold con el médico. Retira su entrada antes de cambiar las preferencias.");
        if (appointmentId.HasValue && !await db.Appointments.AnyAsync(x => x.Id == appointmentId && x.PatientId == patientId && x.DoctorId == doctorId && x.StartUtc > now && (x.Status == AppointmentStatus.Pending || x.Status == AppointmentStatus.Confirmed))) throw new ClinicException("La cita que deseas adelantar ya no está disponible.");
        db.WaitlistEntries.Add(new() { PatientId = patientId, DoctorId = doctorId, FromUtc = from, UntilUtc = until, MinimumNoticeMinutes = minimumNoticeMinutes, AppointmentToAdvanceId = appointmentId });
        Audit(db, a, $"Solicitud de lista de espera para médico #{doctorId}"); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task WithdrawWaitlistAsync(int id)
    {
        var a = await ActorAsync(); await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await LockAsync(db, $"{db.Model.GetDefaultSchema()}:recovery");
        var entry = await db.WaitlistEntries.FindAsync(id) ?? throw new ClinicException("Solicitud no encontrada.");
        if (a.Role == Roles.Patient) { if (entry.PatientId != a.PatientId) throw new UnauthorizedAccessException(); }
        else if (a.Role == Roles.Doctor) ManageAgenda(a, entry.DoctorId); else Staff(a);
        if (entry.Status != WaitlistStatus.Active) throw new ClinicException("La solicitud ya no está activa.");
        entry.Status = WaitlistStatus.Withdrawn;
        Audit(db, a, $"Solicitud de espera #{id} retirada"); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

}


