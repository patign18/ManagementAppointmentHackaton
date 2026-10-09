using System.Data;
using ManagementAppointment.Data;
using ManagementAppointment.Models;
using Microsoft.EntityFrameworkCore;

namespace ManagementAppointment.Services;

// All instances share transaction locks in SQL Server, including multiple web servers.
public class AppointmentRecovery(IDbContextFactory<ClinicDbContext> factory, OfficeClock clock, IConfiguration configuration)
{
    public static DateTime ConfirmationDeadline(DateTime start, DateTime now, int hours, int bookingMinutes)
    {
        var regular = start.AddHours(-hours);
        // A last-minute booking still gets time to confirm; never beyond its start.
        return regular > now ? regular : new[] { start, now.AddMinutes(bookingMinutes) }.Min();
    }

    internal static Task LockAsync(ClinicDbContext db, string resource) => db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51000, 'No se pudo obtener el bloqueo de agenda.', 1;");

    internal static void Notice(ClinicDbContext db, string? userId, string text)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        db.Notifications.Add(new() { UserId = userId, Message = text });
        db.ClinicMails.Add(new() { UserId = userId, Message = text });
    }

    internal static void PatientNotice(ClinicDbContext db, Patient patient, string text, int? appointmentId = null, bool responseLinks = false)
    {
        db.ClinicMails.Add(new() { UserId = patient.UserId ?? "", PatientId = patient.Id, AppointmentId = appointmentId, AppointmentStartUtc = db.Appointments.Local.FirstOrDefault(x => x.Id == appointmentId)?.StartUtc, IncludeResponseLinks = responseLinks, Message = text.Length > 180 ? text[..180] : text, Suppressed = patient.IsDemo });
    }

    public async Task ProcessAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await LockAsync(db, $"{db.Model.GetDefaultSchema()}:recovery");
        var now = DateTime.UtcNow;
        var externalMailEnabled = configuration.GetValue<bool>("Mail:Enabled");
        var reminders = await db.Appointments.Include(x => x.Patient).Include(x => x.Doctor)
            .Where(x => x.Status == AppointmentStatus.Pending && x.Doctor.AutomaticRecovery && x.Doctor.IsActive && x.Doctor.User.IsActive && x.ConfirmationDueUtc > now && x.StartUtc <= now.AddDays(7) && x.ConfirmationReminderStage < 3)
            .OrderBy(x => x.ConfirmationDueUtc).Take(100).ToListAsync(token);
        foreach (var item in reminders)
        {
            var stage = item.StartUtc <= now.AddDays(3) ? 3 : item.StartUtc <= now.AddDays(5) ? 2 : 1;
            if (stage <= item.ConfirmationReminderStage) continue;
            item.ConfirmationReminderStage = stage;
            db.AppointmentHistory.Add(new() { AppointmentId = item.Id, ActorId = "sistema", Action = $"Aviso de confirmación {stage}/3 registrado para envío." });
            PatientNotice(db, item.Patient, $"Aviso {stage}/3. Confirma tu cita del {clock.Local(item.StartUtc):dd/MM h.mm tt} antes de {clock.Local(item.ConfirmationDueUtc!.Value):dd/MM h.mm tt}. Sin confirmación el espacio se reasignará.", item.Id, true);
            item.ReminderQueuedUtc = now;
        }
        await db.SaveChangesAsync(token);
        var due = await db.Appointments.Include(x => x.Doctor).Include(x => x.Patient)
            .Where(x => x.Doctor.AutomaticRecovery && x.Doctor.IsActive && x.Doctor.User.IsActive &&
                (x.Status == AppointmentStatus.Pending || x.Status == AppointmentStatus.Confirmed) &&
                ((x.ConfirmationDueUtc != null && x.ConfirmationDueUtc <= now && x.Status == AppointmentStatus.Pending) || x.StartUtc <= now))
            .OrderBy(x => x.StartUtc).Take(100).ToListAsync(token);
        foreach (var item in due)
        {
            var noShow = item.StartUtc.AddMinutes(item.Doctor.ArrivalGraceMinutes) <= now && item.ArrivalUtc == null;
            var unconfirmed = item.Status == AppointmentStatus.Pending && item.ConfirmationDueUtc <= now && item.ArrivalUtc == null;
            if (!noShow && !unconfirmed) continue;
            await LockAsync(db, $"{db.Model.GetDefaultSchema()}:doctor:{item.DoctorId}:{DateOnly.FromDateTime(clock.Local(item.StartUtc)):yyyyMMdd}");
            item.Status = noShow ? AppointmentStatus.NoShow : AppointmentStatus.Cancelled;
            var reason = noShow ? "Ausencia: venció el margen para registrar llegada" : "Venció el plazo de confirmación";
            db.AppointmentHistory.Add(new() { AppointmentId = item.Id, ActorId = "sistema", Action = $"Liberación automática: {reason}." });
            db.AuditLogs.Add(new() { ActorId = "sistema", Action = $"Cita #{item.Id} liberada: {reason}" });
            PatientNotice(db, item.Patient, $"Tu cita del {clock.Local(item.StartUtc):dd/MM h.mm tt} fue liberada: {reason.ToLowerInvariant()}. Contacta a la oficina si necesitas otra cita.", item.Id);
            Notice(db, item.Doctor.UserId, $"Cita #{item.Id} liberada. El sistema buscará un paciente en lista de espera.");
        }
        await db.SaveChangesAsync(token);

        await db.WaitlistEntries.Where(x => x.Status == WaitlistStatus.Active && x.UntilUtc <= now)
            .ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, WaitlistStatus.Expired), token);
        await db.WaitlistEntries.Where(x => x.Status == WaitlistStatus.Active && x.AppointmentToAdvanceId != null && x.AppointmentToAdvance!.Status != AppointmentStatus.Pending && x.AppointmentToAdvance.Status != AppointmentStatus.Confirmed)
            .ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, WaitlistStatus.Withdrawn), token);

        // Cancelled and rescheduled appointments are retained; a released slot records ownership history.
        var released = await db.Appointments.Include(x => x.Doctor)
            .Where(x => (x.Status == AppointmentStatus.Cancelled || x.Status == AppointmentStatus.NoShow) && x.EndUtc > now &&
                x.Doctor.AutomaticRecovery && x.Doctor.IsActive && x.Doctor.User.IsActive && !db.ReleasedSlots.Any(s => s.SourceAppointmentId == x.Id && s.DoctorId == x.DoctorId && s.StartUtc == x.StartUtc && s.EndUtc == x.EndUtc))
            .OrderBy(x => x.StartUtc).Take(100).ToListAsync(token);
        foreach (var item in released)
            db.ReleasedSlots.Add(new() { SourceAppointmentId = item.Id, DoctorId = item.DoctorId, StartUtc = item.StartUtc, EndUtc = item.EndUtc });
        await db.SaveChangesAsync(token);

        var slots = await db.ReleasedSlots.Include(x => x.SourceAppointment).Include(x => x.Doctor)
            .Where(x => !x.Closed).OrderBy(x => x.StartUtc).Take(100).ToListAsync(token);
        foreach (var slot in slots)
        {
            var d = slot.Doctor;
            var start = slot.StartUtc > now ? slot.StartUtc : now;
            if (slot.EndUtc.AddMinutes(-d.MinimumRecoveredMinutes) <= now || !d.IsActive || !await db.Users.AnyAsync(x => x.Id == d.UserId && x.IsActive, token)) { slot.Closed = true; continue; }
            if (!d.AutomaticRecovery) continue;
            await LockAsync(db, $"{db.Model.GetDefaultSchema()}:doctor:{d.Id}:{DateOnly.FromDateTime(clock.Local(slot.StartUtc)):yyyyMMdd}");
            if (await db.Appointments.AnyAsync(x => x.DoctorId == d.Id && x.StartUtc < slot.EndUtc && x.EndUtc > start && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow, token)) { slot.Closed = true; continue; }
            if (await db.ScheduleExceptions.AnyAsync(x => x.DoctorId == d.Id && x.StartUtc < slot.EndUtc && x.EndUtc > start, token)) { slot.Closed = true; continue; }
            var schedules = await db.DoctorSchedules.Where(x => x.DoctorId == d.Id).ToListAsync(token);
            var localStart = clock.Local(start); var localEnd = clock.Local(slot.EndUtc);
            if (localStart.Date != localEnd.Date || !schedules.Any(x => x.Day == localStart.DayOfWeek && x.Start <= TimeOnly.FromDateTime(localStart) && x.End >= TimeOnly.FromDateTime(localEnd))) { slot.Closed = true; continue; }
            var entries = await db.WaitlistEntries.Include(x => x.Patient).Include(x => x.AppointmentToAdvance)
                .Where(x => x.DoctorId == d.Id && x.Status == WaitlistStatus.Active && x.FromUtc <= start && x.UntilUtc >= slot.EndUtc &&
                    x.Patient.IsActive && x.Patient.AutomaticReassignmentConsent && (x.Patient.IsDemo || (externalMailEnabled && x.Patient.ContactConsent && x.Patient.Email != null)) &&
                    x.PatientId != slot.SourceAppointment.PatientId && !db.SlotAssignments.Any(o => o.WaitlistEntryId == x.Id && o.ReleasedSlotId == slot.Id))
                .OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).ToListAsync(token);
            foreach (var entry in entries)
            {
                var advancing = entry.AppointmentToAdvance;
                if (entry.AppointmentToAdvanceId.HasValue && (advancing == null || advancing.PatientId != entry.PatientId || advancing.DoctorId != d.Id || advancing.StartUtc < slot.EndUtc || advancing.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))) continue;
                if (now.AddMinutes(entry.MinimumNoticeMinutes) >= slot.EndUtc.AddMinutes(-d.MinimumRecoveredMinutes)) continue;
                if (slot.StartUtc > now && now.AddMinutes(entry.MinimumNoticeMinutes) > slot.StartUtc) continue;
                await LockAsync(db, $"{db.Model.GetDefaultSchema()}:patient:{entry.PatientId}");
                if (await db.Appointments.AnyAsync(x => x.PatientId == entry.PatientId && x.Id != entry.AppointmentToAdvanceId && x.StartUtc < slot.EndUtc && x.EndUtc > start && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.NoShow, token)) continue;
                var assignedStart = new[] { slot.StartUtc, DateTime.UtcNow.AddMinutes(entry.MinimumNoticeMinutes) }.Max();
                if (assignedStart.AddMinutes(d.MinimumRecoveredMinutes) > slot.EndUtc) continue;
                var appointment = advancing ?? new Appointment { DoctorId = d.Id, PatientId = entry.PatientId };
                if (advancing != null)
                {
                    if (!await db.ReleasedSlots.AnyAsync(x => x.SourceAppointmentId == advancing.Id && x.DoctorId == d.Id && x.StartUtc == advancing.StartUtc && x.EndUtc == advancing.EndUtc, token))
                        db.ReleasedSlots.Add(new() { SourceAppointmentId = advancing.Id, DoctorId = d.Id, StartUtc = advancing.StartUtc, EndUtc = advancing.EndUtc });
                    db.AppointmentHistory.Add(new() { AppointmentId = advancing.Id, ActorId = "sistema", Action = $"Cita adelantada desde {clock.Local(advancing.StartUtc):dd/MM h.mm tt}. Se liberó su horario anterior." });
                }
                else db.Appointments.Add(appointment);
                appointment.StartUtc = assignedStart; appointment.EndUtc = slot.EndUtc; appointment.Status = AppointmentStatus.Confirmed;
                appointment.ConfirmationDueUtc = null; appointment.ReminderQueuedUtc = null; appointment.ChangeRequest = null;
                db.AppointmentHistory.Add(new() { Appointment = appointment, ActorId = "sistema", Action = $"Asignación automática autorizada en hold #{entry.Id}; espacio recuperado de cita #{slot.SourceAppointmentId}." });
                entry.Status = WaitlistStatus.Assigned; slot.Closed = true;
                await db.SaveChangesAsync(token);
                db.SlotAssignments.Add(new() { ReleasedSlotId = slot.Id, WaitlistEntryId = entry.Id, AppointmentId = appointment.Id });
                PatientNotice(db, entry.Patient, $"Cita {(advancing == null ? "asignada" : "adelantada")} automáticamente: {clock.Local(assignedStart):dd/MM h.mm tt}–{clock.Local(slot.EndUtc):h.mm tt}. La oficina ha reservado este espacio para ti.", appointment.Id, true);
                Notice(db, d.UserId, $"Espacio recuperado: cita #{appointment.Id} asignada automáticamente desde la lista de espera.");
                db.AuditLogs.Add(new() { ActorId = "sistema", Action = $"Espacio #{slot.Id} asignado a solicitud #{entry.Id}: cita #{appointment.Id}" });
                await db.SaveChangesAsync(token);
                break;
            }
        }
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
    }
}

public class AppointmentRecoveryWorker(IServiceScopeFactory scopes, ILogger<AppointmentRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AppointmentRecovery>().ProcessAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<ClinicMailDispatcher>().DispatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("No se pudo procesar la lista de espera ({Type}). Comprueba la conexión y ejecuta --clinic-upgrade si el esquema no está actualizado.", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}


