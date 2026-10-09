namespace ManagementAppointment.Services;

public sealed class ClinicException(string message) : InvalidOperationException(message);
