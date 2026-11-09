/*
   Ecmanage custom build DNN 9.11.2: database performance fixes (analysis production 9-11-2026).

   MANUAL SCRIPT. DNN never runs this file itself (it is outside Providers\DataProviders\SqlDataProvider and has no
   version number in its name). Run it per environment with SSMS against the DNN database, test/acceptance first.
   Assumes databaseOwner = dbo and an empty objectQualifier (as on production ECM2023).

   Run outside office hours: creating the EventLog index briefly locks the EventLog table (Standard Edition, no ONLINE).
   A DNN upgrade may overwrite the stored procedures below; run this script again after an upgrade.

   Contents
   1. EnsureLocalizationExists  - fix: @MasterLanguage was not assigned in the last ELSE branch
   2. Index on EventLog         - (LogConfigID, LogCreateDate DESC, LogEventID DESC)
   3. PurgeEventLog             - seek per log type instead of sorting the whole table; actually purges again
   4. PurgeEventLogBatchCount   - 150000 -> 10000
   5. GetTabPaths               - no catch-all, reads Tabs instead of vw_Tabs
   6. Optional one-time cleanup of the EventLog backlog (commented out)
   7. Checks
*/

/****************************************************************
 * SPROC: EnsureLocalizationExists
 * Fix: the last ELSE branch did not assign @MasterLanguage ("SELECT TOP (1) CultureCode"), so a portal without a
 * localization in its default language and without en-US never got the missing localization.
 ****************************************************************/

IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.[EnsureLocalizationExists]') AND type in (N'P', N'PC'))
	DROP PROCEDURE dbo.[EnsureLocalizationExists]
GO

CREATE PROCEDURE dbo.[EnsureLocalizationExists]
	@PortalID       Int,
	@CultureCode	nvarchar(10)
AS
BEGIN
	DECLARE @MasterLanguage nvarchar(10) = Null;
	DECLARE @LocalizationExists bit = 0;

	IF NOT EXISTS (SELECT * FROM dbo.[Languages] L
					JOIN dbo.[PortalLanguages] P ON L.LanguageID = P.LanguageID
					WHERE P.PortalID = @PortalID AND L.CultureCode = @CultureCode)
		RETURN; -- language does not exist for this portal

	IF EXISTS (SELECT * FROM dbo.[PortalLocalization]
				WHERE CultureCode = @CultureCode AND PortalID = @PortalID)
		RETURN; -- already localized

	IF EXISTS (SELECT * FROM dbo.[PortalLocalization] L
					JOIN dbo.[Portals] P ON L.PortalID = P.PortalID AND L.CultureCode = P.DefaultLanguage
					WHERE P.PortalID = @PortalID)
		SELECT @MasterLanguage = DefaultLanguage
		FROM dbo.[Portals]
		WHERE PortalID = @PortalID
	ELSE IF EXISTS (SELECT * FROM dbo.[PortalLocalization]
					WHERE CultureCode = 'en-US' and PortalID = @PortalID)
		SET @MasterLanguage = 'en-US'
	ELSE -- neither default nor system language available: take the language that was assigned first
		SELECT TOP (1) @MasterLanguage = CultureCode
		FROM dbo.[PortalLocalization]
		WHERE PortalID = @PortalID
		ORDER BY CreatedOnDate ASC, CultureCode ASC;

	IF NOT (@MasterLanguage Is Null OR @MasterLanguage LIKE @CultureCode)
	BEGIN  -- copy localized values from (existing and different) master language:
		INSERT INTO dbo.[PortalLocalization]
		(	PortalId,
			CultureCode,
			PortalName,
			LogoFile,
			FooterText,
			Description,
			KeyWords,
			BackgroundFile,
			HomeTabId,
			LoginTabId,
			UserTabId,
			AdminTabId,
			RegisterTabId,
			SearchTabId,
			CreatedByUserID,
			CreatedOnDate,
			LastModifiedByUserID,
			LastModifiedOnDate
		) SELECT
			PortalId,
			@CultureCode,
			PortalName,
			LogoFile,
			FooterText,
			Description,
			KeyWords,
			BackgroundFile,
			HomeTabId,
			LoginTabId,
			UserTabId,
			AdminTabId,
			RegisterTabId,
			SearchTabId,
			-1,
			GETDATE(),
			-1,
			GETDATE()
		 FROM dbo.[PortalLocalization]
		 WHERE PortalID = @PortalID AND CultureCode = @MasterLanguage;

		-- copy missing localized settings:
		DECLARE
			@LocalPortalSettings TABLE(
		    [PortalID]             INT             NOT NULL,
		    [CultureCode]          NVARCHAR (10)   NOT NULL,
		    [SettingName]          NVARCHAR (50)   NOT NULL,
		    [SettingValue]         NVARCHAR (2000) NULL
		);

		INSERT INTO @LocalPortalSettings
		(
			PortalID,
			CultureCode,
			SettingName,
			SettingValue
		)
		SELECT
			PortalID,
			CultureCode,
			SettingName,
			SettingValue
		FROM dbo.[PortalSettings]
		WHERE PortalID = @PortalID AND CultureCode = @CultureCode

		MERGE INTO @LocalPortalSettings target
		USING (SELECT * FROM dbo.[PortalSettings]
				WHERE PortalId = @PortalID and CultureCode = @MasterLanguage) source
		ON (target.SettingName = source.SettingName)
		WHEN NOT MATCHED THEN
			INSERT (
				PortalID,
				CultureCode,
				SettingName,
				SettingValue)
			VALUES (
				source.PortalID,
				@CultureCode,
				source.SettingName,
				source.SettingValue
			);

		MERGE INTO dbo.[PortalSettings]  target
		USING (SELECT * FROM @LocalPortalSettings) source
		ON (target.PortalID = source.PortalID AND
			target.CultureCode = source.CultureCode AND
			target.SettingName = source.SettingName)
		WHEN NOT MATCHED THEN
			INSERT (
				PortalID,
				CultureCode,
				SettingName,
				SettingValue,
				CreatedByUserID,
				CreatedOnDate,
				LastModifiedByUserID,
				LastModifiedOnDate)
			VALUES (
				source.PortalID,
				@CultureCode,
				source.SettingName,
				source.SettingValue,
				-1,
				GETDATE(),
				-1,
				GETDATE()
			);
	END;
END
GO

/****************************************************************
 * INDEX: EventLog (LogConfigID, LogCreateDate DESC, LogEventID DESC)
 * Lets PurgeEventLog find the newest entries per log type with an index seek.
 ****************************************************************/

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.[EventLog]') AND name = N'IX_EventLog_LogConfigID_LogCreateDate')
	CREATE NONCLUSTERED INDEX [IX_EventLog_LogConfigID_LogCreateDate]
		ON dbo.[EventLog] ([LogConfigID] ASC, [LogCreateDate] DESC, [LogEventID] DESC)
GO

/****************************************************************
 * SPROC: PurgeEventLog
 * The 9.3.0 version used TOP(@PurgeBatchCount) without ORDER BY on top of ROW_NUMBER() over the whole EventLog
 * table: every call sorted the complete table (2.2 s CPU, 312,000 reads on production, ~1,000 calls a day because
 * Purge Log Buffer runs on every web server) and it always selected the same rows, so log types with a higher
 * LogConfigID were never purged (LOGIN_FAILURE: 786,517 rows with KeepMostRecent 10).
 * This version determines the cut-off per log type with an index seek and only deletes below it. Same meaning:
 * keep the KeepMostRecent newest entries per LogConfigID. DNN calls it without parameters; @BatchCount can be used
 * for a manual cleanup. Returns the number of deleted rows.
 ****************************************************************/

IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.[PurgeEventLog]') AND type in (N'P', N'PC'))
	DROP PROCEDURE dbo.[PurgeEventLog]
GO

CREATE PROCEDURE dbo.[PurgeEventLog]
	@BatchCount INT = NULL
AS
BEGIN
	SET NOCOUNT ON;

	DECLARE @PurgeBatchCount INT = ISNULL(@BatchCount, ISNULL(
		(SELECT CAST(SettingValue AS INT)
		   FROM dbo.[HostSettings]
		  WHERE SettingName = N'PurgeEventLogBatchCount' AND ISNUMERIC(SettingValue) > 0), 150000));

	DECLARE @Deleted INT = 0,
			@ConfigID INT,
			@Keep INT,
			@CutoffDate DATETIME,
			@CutoffID BIGINT;

	DECLARE cfg CURSOR LOCAL FAST_FORWARD FOR
		SELECT ID, KeepMostRecent
		  FROM dbo.[EventLogConfig]
		 WHERE KeepMostRecent >= 0; -- -1 = keep everything

	OPEN cfg;
	FETCH NEXT FROM cfg INTO @ConfigID, @Keep;

	WHILE @@FETCH_STATUS = 0 AND @Deleted < @PurgeBatchCount
	BEGIN
		SELECT @CutoffDate = NULL, @CutoffID = NULL;

		-- The newest entry that does not have to be kept (position KeepMostRecent + 1).
		SELECT @CutoffDate = LogCreateDate, @CutoffID = LogEventID
		  FROM dbo.[EventLog]
		 WHERE LogConfigID = @ConfigID
		 ORDER BY LogCreateDate DESC, LogEventID DESC
		 OFFSET @Keep ROWS FETCH NEXT 1 ROWS ONLY;

		IF @CutoffDate IS NOT NULL
		BEGIN
			DELETE TOP (@PurgeBatchCount - @Deleted)
			  FROM dbo.[EventLog]
			 WHERE LogConfigID = @ConfigID
			   AND (LogCreateDate < @CutoffDate
					OR (LogCreateDate = @CutoffDate AND LogEventID <= @CutoffID));

			SET @Deleted += @@ROWCOUNT;
		END;

		FETCH NEXT FROM cfg INTO @ConfigID, @Keep;
	END;

	CLOSE cfg;
	DEALLOCATE cfg;

	RETURN @Deleted;
END
GO

/****************************************************************
 * HOST SETTING: PurgeEventLogBatchCount 150000 -> 10000
 * Keeps each purge run short. The existing backlog is removed in steps of 10,000 per run
 * (7 web servers x every 10 minutes).
 ****************************************************************/

UPDATE dbo.[HostSettings]
   SET SettingValue = N'10000', LastModifiedOnDate = GETDATE()
 WHERE SettingName = N'PurgeEventLogBatchCount' AND SettingValue = N'150000'
GO

/****************************************************************
 * SPROC: GetTabPaths
 * The catch-all "... OR @PortalID IS NULL" on vw_Tabs read all tabs on every call (88 ms CPU, 49,000 reads on
 * production with 636,518 tabs). Split into two branches and read Tabs directly: vw_Tabs is Tabs with a
 * one-to-one LEFT JOIN on ContentItems and no filter, so the rows are the same.
 ****************************************************************/

IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.[GetTabPaths]') AND type in (N'P', N'PC'))
	DROP PROCEDURE dbo.[GetTabPaths]
GO

CREATE PROCEDURE dbo.[GetTabPaths]
	@PortalID		INT,
	@CultureCode	NVARCHAR(10)
AS
BEGIN
	SET NOCOUNT ON;

	IF @PortalID IS NULL
		SELECT TabID, PortalID, TabPath
		  FROM dbo.[Tabs];
	ELSE
		SELECT TabID, PortalID, TabPath
		  FROM dbo.[Tabs]
		 WHERE PortalID = @PortalID
		   AND (CultureCode = @CultureCode OR CultureCode IS NULL OR ISNULL(@CultureCode, '') = '');
END
GO

/****************************************************************
 * 6. OPTIONAL: one-time cleanup of the EventLog backlog (~2.2 million rows on production).
 * Without this the scheduled purges remove the backlog in steps of 10,000 per run (7 servers x every 10 minutes,
 * about 5 hours). Run outside office hours and watch the transaction log.
 ****************************************************************/
/*
DECLARE @n INT = 1, @total INT = 0;
WHILE @n > 0
BEGIN
	EXEC @n = dbo.PurgeEventLog @BatchCount = 10000;
	SET @total += @n;
	RAISERROR('Deleted: %d (total %d)', 0, 1, @n, @total) WITH NOWAIT;
	WAITFOR DELAY '00:00:01';
END;
*/
GO

/****************************************************************
 * 7. Checks
 ****************************************************************/

-- Rows per log type: for KeepMostRecent >= 0 the count should end up at or below the limit.
SELECT elc.ID, elc.LogTypeKey, elc.KeepMostRecent, COUNT_BIG(el.LogEventID) AS row_count
  FROM dbo.EventLogConfig elc
  LEFT JOIN dbo.EventLog el ON el.LogConfigID = elc.ID
 GROUP BY elc.ID, elc.LogTypeKey, elc.KeepMostRecent
HAVING COUNT_BIG(el.LogEventID) > 0
 ORDER BY row_count DESC;

-- Cost per call (production 9-11-2026 before the fix: PurgeEventLog 2,233 ms CPU, GetTabPaths 88.7 ms CPU).
SELECT OBJECT_NAME(object_id) AS procedure_name, execution_count,
       total_worker_time / NULLIF(execution_count, 0) / 1000.0 AS avg_cpu_ms,
       total_logical_reads / NULLIF(execution_count, 0) AS avg_logical_reads, cached_time
  FROM sys.dm_exec_procedure_stats
 WHERE database_id = DB_ID() AND OBJECT_NAME(object_id) IN ('PurgeEventLog', 'GetTabPaths', 'EnsureLocalizationExists');
GO
