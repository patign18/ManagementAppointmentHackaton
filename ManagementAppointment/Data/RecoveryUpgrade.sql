SET XACT_ABORT ON;
IF COL_LENGTH('ma.Patients', 'Address') IS NULL
 ALTER TABLE ma.Patients ADD Address nvarchar(200) NOT NULL CONSTRAINT DF_Patient_Address DEFAULT '',
 Municipality nvarchar(80) NOT NULL CONSTRAINT DF_Patient_Municipality DEFAULT '',
 PostalCode nvarchar(12) NOT NULL CONSTRAINT DF_Patient_Postal DEFAULT '',
 ContactConsent bit NOT NULL CONSTRAINT DF_Patient_Contact DEFAULT 0,
 AutomaticReassignmentConsent bit NOT NULL CONSTRAINT DF_Patient_Automatic DEFAULT 0,
 IsDemo bit NOT NULL CONSTRAINT DF_Patient_Demo DEFAULT 0;
IF COL_LENGTH('ma.Patients', 'RegisteredByDoctorId') IS NULL
 ALTER TABLE ma.Patients ADD RegisteredByDoctorId int NULL;
IF COL_LENGTH('ma.Doctors', 'AutomaticRecovery') IS NULL
BEGIN
 ALTER TABLE ma.Doctors ADD AutomaticRecovery bit NOT NULL CONSTRAINT DF_Doctors_Recovery DEFAULT 0,
 ConfirmationHours int NOT NULL CONSTRAINT DF_Doctors_Confirmation DEFAULT 24,
 ArrivalGraceMinutes int NOT NULL CONSTRAINT DF_Doctors_Grace DEFAULT 15,
 OfferMinutes int NOT NULL CONSTRAINT DF_Doctors_Offer DEFAULT 15,
 MinimumRecoveredMinutes int NOT NULL CONSTRAINT DF_Doctors_Minimum DEFAULT 10;
END;
IF COL_LENGTH('ma.Appointments', 'ConfirmationDueUtc') IS NULL
 ALTER TABLE ma.Appointments ADD ConfirmationDueUtc datetime2 NULL;
IF COL_LENGTH('ma.Appointments', 'ReminderQueuedUtc') IS NULL
 ALTER TABLE ma.Appointments ADD ReminderQueuedUtc datetime2 NULL;
IF OBJECT_ID('ma.WaitlistEntries') IS NULL
BEGIN
 CREATE TABLE ma.WaitlistEntries (
 Id int IDENTITY PRIMARY KEY, PatientId int NOT NULL, DoctorId int NOT NULL,
 FromUtc datetime2 NOT NULL, UntilUtc datetime2 NOT NULL, MinimumNoticeMinutes int NOT NULL,
 CreatedUtc datetime2 NOT NULL, Status int NOT NULL, Version rowversion NOT NULL,
 CONSTRAINT FK_Waitlist_Patient FOREIGN KEY(PatientId) REFERENCES ma.Patients(Id),
 CONSTRAINT FK_Waitlist_Doctor FOREIGN KEY(DoctorId) REFERENCES ma.Doctors(Id),
 CONSTRAINT CK_Waitlist_Time CHECK (FromUtc < UntilUtc AND MinimumNoticeMinutes BETWEEN 0 AND 10080 AND Status BETWEEN 0 AND 3));
 CREATE INDEX IX_WaitlistEntries_DoctorId_Status_CreatedUtc ON ma.WaitlistEntries(DoctorId, Status, CreatedUtc);
 CREATE UNIQUE INDEX IX_WaitlistEntries_PatientId_DoctorId ON ma.WaitlistEntries(PatientId, DoctorId) WHERE Status=0;
END;
IF COL_LENGTH('ma.WaitlistEntries', 'AppointmentToAdvanceId') IS NULL
 ALTER TABLE ma.WaitlistEntries ADD AppointmentToAdvanceId int NULL CONSTRAINT FK_Waitlist_Advance REFERENCES ma.Appointments(Id);
IF OBJECT_ID('ma.ReleasedSlots') IS NULL
BEGIN
 CREATE TABLE ma.ReleasedSlots (
 Id int IDENTITY PRIMARY KEY, SourceAppointmentId int NOT NULL, DoctorId int NOT NULL,
 StartUtc datetime2 NOT NULL, EndUtc datetime2 NOT NULL, Closed bit NOT NULL,
 CONSTRAINT FK_Released_Appointment FOREIGN KEY(SourceAppointmentId) REFERENCES ma.Appointments(Id));
END;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('ma.ReleasedSlots') AND name='IX_ReleasedSlots_SourceAppointmentId')
 DROP INDEX IX_ReleasedSlots_SourceAppointmentId ON ma.ReleasedSlots;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('ma.ReleasedSlots') AND name='IX_ReleasedSlots_SourceAppointmentId_DoctorId_StartUtc_EndUtc')
 CREATE UNIQUE INDEX IX_ReleasedSlots_SourceAppointmentId_DoctorId_StartUtc_EndUtc ON ma.ReleasedSlots(SourceAppointmentId, DoctorId, StartUtc, EndUtc);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('ma.ReleasedSlots') AND name='FK_Released_Doctor')
 ALTER TABLE ma.ReleasedSlots ADD CONSTRAINT FK_Released_Doctor FOREIGN KEY(DoctorId) REFERENCES ma.Doctors(Id);
IF OBJECT_ID('ma.SlotAssignments') IS NULL
BEGIN
 CREATE TABLE ma.SlotAssignments (
 Id int IDENTITY PRIMARY KEY, ReleasedSlotId int NOT NULL, WaitlistEntryId int NOT NULL,
 AppointmentId int NOT NULL, CreatedUtc datetime2 NOT NULL,
 CONSTRAINT FK_Assignment_Slot FOREIGN KEY(ReleasedSlotId) REFERENCES ma.ReleasedSlots(Id),
 CONSTRAINT FK_Assignment_Entry FOREIGN KEY(WaitlistEntryId) REFERENCES ma.WaitlistEntries(Id),
 CONSTRAINT FK_Assignment_Appointment FOREIGN KEY(AppointmentId) REFERENCES ma.Appointments(Id));
 CREATE UNIQUE INDEX IX_SlotAssignments_ReleasedSlotId ON ma.SlotAssignments(ReleasedSlotId);
 CREATE UNIQUE INDEX IX_SlotAssignments_WaitlistEntryId ON ma.SlotAssignments(WaitlistEntryId);
END;
IF OBJECT_ID('ma.ClinicMails') IS NULL
BEGIN
 CREATE TABLE ma.ClinicMails (
 Id bigint IDENTITY PRIMARY KEY, UserId nvarchar(450) NOT NULL, Message nvarchar(180) NOT NULL,
 CreatedUtc datetime2 NOT NULL, SentUtc datetime2 NULL, LastAttemptUtc datetime2 NULL, Attempts int NOT NULL);
 CREATE INDEX IX_ClinicMails_SentUtc_CreatedUtc ON ma.ClinicMails(SentUtc, CreatedUtc);
END;
IF COL_LENGTH('ma.ClinicMails', 'PatientId') IS NULL
 ALTER TABLE ma.ClinicMails ADD PatientId int NULL, AppointmentId int NULL,
 IncludeResponseLinks bit NOT NULL CONSTRAINT DF_Mail_Links DEFAULT 0,
 Suppressed bit NOT NULL CONSTRAINT DF_Mail_Suppressed DEFAULT 0;
IF COL_LENGTH('ma.ClinicMails', 'AppointmentStartUtc') IS NULL
 ALTER TABLE ma.ClinicMails ADD AppointmentStartUtc datetime2 NULL;

IF COL_LENGTH('ma.Appointments', 'ConfirmationReminderStage') IS NULL
BEGIN
 ALTER TABLE ma.Appointments ADD ConfirmationReminderStage int NOT NULL CONSTRAINT DF_Appointments_ReminderStage DEFAULT 0;
 UPDATE ma.Doctors SET ConfirmationHours=48;
 UPDATE a SET ConfirmationDueUtc=CASE WHEN DATEADD(hour,-48,a.StartUtc)>SYSUTCDATETIME() THEN DATEADD(hour,-48,a.StartUtc) ELSE CASE WHEN DATEADD(minute,d.OfferMinutes,SYSUTCDATETIME())<a.StartUtc THEN DATEADD(minute,d.OfferMinutes,SYSUTCDATETIME()) ELSE a.StartUtc END END
 FROM ma.Appointments a JOIN ma.Doctors d ON d.Id=a.DoctorId WHERE a.Status=0 AND a.StartUtc>SYSUTCDATETIME() AND d.AutomaticRecovery=1;
END;