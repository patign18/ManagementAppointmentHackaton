namespace ManagementAppointment.Services;

public class OfficeClock(IConfiguration configuration)
{
    public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(configuration["Office:TimeZone"] ?? "America/La_Paz");
    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public DateOnly Today => DateOnly.FromDateTime(Now);
    public DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
    public DateTime Utc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local) || Zone.IsAmbiguousTime(local)) throw new ClinicException("El horario no es válido en la zona de la oficina.");
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }
    public DateTime Utc(DateOnly date, TimeOnly time) => Utc(date.ToDateTime(time));
}

