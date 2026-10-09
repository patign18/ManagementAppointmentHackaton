using Microsoft.EntityFrameworkCore;

namespace ManagementAppointment.Services;

public record ReassignmentTrace(int AppointmentId, string PatientName, DateTime SlotStartUtc, DateTime AssignedUtc);

public partial class ClinicService
{
    public async Task<List<ReassignmentTrace>> ReassignmentTraceAsync(int sourceAppointmentId)
    {
        var actor = await ActorAsync();
        await using var db = await factory.CreateDbContextAsync();
        if (!await Scope(db.Appointments, actor).AnyAsync(x => x.Id == sourceAppointmentId)) throw new UnauthorizedAccessException();
        return await db.SlotAssignments.AsNoTracking()
            .Where(x => x.ReleasedSlot.SourceAppointmentId == sourceAppointmentId)
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => new ReassignmentTrace(x.AppointmentId, x.WaitlistEntry.Patient.FullName, x.ReleasedSlot.StartUtc, x.CreatedUtc))
            .ToListAsync();
    }
}
