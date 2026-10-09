using ManagementAppointment.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ManagementAppointment.Data;

public class ClinicDbContext(DbContextOptions<ClinicDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<Specialty> Specialties => Set<Specialty>();
    public DbSet<DoctorSchedule> DoctorSchedules => Set<DoctorSchedule>();
    public DbSet<ScheduleException> ScheduleExceptions => Set<ScheduleException>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentHistory> AppointmentHistory => Set<AppointmentHistory>();
    public DbSet<ClinicNotification> Notifications => Set<ClinicNotification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();
    public DbSet<ReleasedSlot> ReleasedSlots => Set<ReleasedSlot>();
    public DbSet<SlotAssignment> SlotAssignments => Set<SlotAssignment>();
    public DbSet<ClinicMail> ClinicMails => Set<ClinicMail>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.HasDefaultSchema("ma");
        model.Entity<ApplicationUser>().HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
        model.Entity<Patient>().HasIndex(x => x.UserId).IsUnique().HasFilter("[UserId] IS NOT NULL");
        model.Entity<Patient>().HasIndex(x => x.DocumentNumber).IsUnique().HasFilter("[DocumentNumber] IS NOT NULL");
        model.Entity<Patient>().HasOne(x => x.User).WithOne().HasForeignKey<Patient>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Doctor>().HasIndex(x => x.UserId).IsUnique();
        model.Entity<Doctor>().Property(x => x.LastMinuteConfirmationMinutes).HasColumnName("OfferMinutes");
        model.Entity<Doctor>().HasOne(x => x.User).WithOne().HasForeignKey<Doctor>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Doctor>().HasOne(x => x.Specialty).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<Specialty>().HasIndex(x => x.Name).IsUnique();
        model.Entity<Doctor>().ToTable(t => t.HasCheckConstraint("CK_Doctor_Duration", "[ConsultationMinutes] BETWEEN 10 AND 180"));
        model.Entity<DoctorSchedule>().ToTable(t => t.HasCheckConstraint("CK_Schedule_Time", "[Start] < [End] AND [Day] BETWEEN 0 AND 6"));
        model.Entity<ScheduleException>().ToTable(t => t.HasCheckConstraint("CK_Exception_Time", "[StartUtc] < [EndUtc]"));
        model.Entity<Appointment>().ToTable(t => t.HasCheckConstraint("CK_Appointment_Time", "[StartUtc] < [EndUtc] AND [Status] BETWEEN 0 AND 6"));
        model.Entity<Appointment>().HasIndex(x => new { x.DoctorId, x.StartUtc, x.EndUtc });
        model.Entity<Appointment>().HasIndex(x => new { x.PatientId, x.StartUtc });
        model.Entity<Appointment>().HasOne(x => x.Patient).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<Appointment>().HasOne(x => x.Doctor).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<DoctorSchedule>().HasIndex(x => new { x.DoctorId, x.Day });
        model.Entity<ScheduleException>().HasIndex(x => new { x.DoctorId, x.StartUtc });
        model.Entity<ClinicNotification>().HasIndex(x => new { x.UserId, x.IsRead, x.CreatedUtc });
        model.Entity<ClinicNotification>().HasOne(x => x.User).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<AppointmentHistory>().HasIndex(x => new { x.AppointmentId, x.CreatedUtc });
        model.Entity<WaitlistEntry>().HasOne(x => x.Patient).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<WaitlistEntry>().HasOne(x => x.Doctor).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<WaitlistEntry>().HasOne(x => x.AppointmentToAdvance).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<WaitlistEntry>().HasIndex(x => new { x.DoctorId, x.Status, x.CreatedUtc });
        model.Entity<WaitlistEntry>().HasIndex(x => new { x.PatientId, x.DoctorId }).IsUnique().HasFilter("[Status] = 0");
        model.Entity<ReleasedSlot>().HasOne(x => x.SourceAppointment).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<ReleasedSlot>().HasOne(x => x.Doctor).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<ReleasedSlot>().HasIndex(x => new { x.SourceAppointmentId, x.DoctorId, x.StartUtc, x.EndUtc }).IsUnique();
        model.Entity<SlotAssignment>().HasOne(x => x.ReleasedSlot).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<SlotAssignment>().HasOne(x => x.WaitlistEntry).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<SlotAssignment>().HasOne(x => x.Appointment).WithMany().OnDelete(DeleteBehavior.Restrict);
        model.Entity<SlotAssignment>().HasIndex(x => x.ReleasedSlotId).IsUnique();
        model.Entity<SlotAssignment>().HasIndex(x => x.WaitlistEntryId).IsUnique();
        model.Entity<ClinicMail>().HasIndex(x => new { x.SentUtc, x.CreatedUtc });
    }
}
