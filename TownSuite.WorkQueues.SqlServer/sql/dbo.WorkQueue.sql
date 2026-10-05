-- Create the workqueue table if it does not already exist.
IF OBJECT_ID(N'[dbo].[workqueue]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[workqueue] (
        [id]               INT              IDENTITY(1,1) NOT NULL,
        [messageid]        UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_workqueue_messageid] DEFAULT (NEWID()),
        [timecreatedutc]   DATETIME         NOT NULL CONSTRAINT [DF_workqueue_timecreatedutc] DEFAULT (GETUTCDATE()),
        [channel]          NVARCHAR(500)    NOT NULL,
        [payload]          NVARCHAR(MAX)    NOT NULL,
        [timeprocessedutc] DATETIME         NULL,
        [failedat]         DATETIME         NULL,
        [retrycount]       INT              NOT NULL CONSTRAINT [DF_workqueue_retrycount] DEFAULT (0),
        [scheduledfor]     DATETIME         NULL,
        [faultdispatchedat] DATETIME        NULL,
        [lasterror]        NVARCHAR(MAX)    NULL,
        CONSTRAINT [PK_workqueue] PRIMARY KEY CLUSTERED ([id] ASC)
    )
END
GO
-- Add failedat column if upgrading from a schema that pre-dates it.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[workqueue]') AND name = N'failedat'
)
BEGIN
    ALTER TABLE [dbo].[workqueue] ADD [failedat] DATETIME NULL
END
GO
-- Add retrycount column if upgrading from a schema that pre-dates it.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[workqueue]') AND name = N'retrycount'
)
BEGIN
    ALTER TABLE [dbo].[workqueue]
        ADD [retrycount] INT NOT NULL CONSTRAINT [DF_workqueue_retrycount_add] DEFAULT (0)
END
GO
-- Add scheduledfor column if upgrading from a schema that pre-dates it.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[workqueue]') AND name = N'scheduledfor'
)
BEGIN
    ALTER TABLE [dbo].[workqueue] ADD [scheduledfor] DATETIME NULL
END
GO
-- Add messageid column if upgrading from a schema that pre-dates it.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[workqueue]') AND name = N'messageid'
)
BEGIN
    ALTER TABLE [dbo].[workqueue]
        ADD [messageid] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_workqueue_messageid_add] DEFAULT (NEWID()) WITH VALUES
END
GO
-- Add lasterror column if upgrading from a schema that pre-dates it.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[workqueue]') AND name = N'lasterror'
)
BEGIN
    ALTER TABLE [dbo].[workqueue] ADD [lasterror] NVARCHAR(MAX) NULL
END
GO
-- Add faultdispatchedat column if upgrading from a schema that pre-dates it. Existing
-- dead-letters are marked as already notified so the upgrade does not send faults for them.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[workqueue]') AND name = N'faultdispatchedat'
)
BEGIN
    ALTER TABLE [dbo].[workqueue] ADD [faultdispatchedat] DATETIME NULL
    EXEC(N'UPDATE [dbo].[workqueue] SET [faultdispatchedat] = [failedat] WHERE [failedat] IS NOT NULL')
END
GO
-- Widen channel column if upgrading from nvarchar(50).
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[workqueue]')
      AND name = N'channel'
      AND max_length < 1000   -- nvarchar stores 2 bytes per char; 500 chars = 1000 bytes
)
BEGIN
    ALTER TABLE [dbo].[workqueue] ALTER COLUMN [channel] NVARCHAR(500) NOT NULL
END
GO
-- Create the filtered covering index if it does not already exist.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_workqueue_channel_unprocessed'
      AND object_id = OBJECT_ID(N'[dbo].[workqueue]')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_workqueue_channel_unprocessed]
    ON [dbo].[workqueue] ([channel] ASC, [timecreatedutc] ASC)
    WHERE ([timeprocessedutc] IS NULL AND [failedat] IS NULL)
END
GO
-- Filtered index over dead-lettered rows for replay and queue statistics.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_workqueue_channel_deadlettered'
      AND object_id = OBJECT_ID(N'[dbo].[workqueue]')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_workqueue_channel_deadlettered]
    ON [dbo].[workqueue] ([channel] ASC, [messageid] ASC)
    WHERE ([failedat] IS NOT NULL)
END
GO
-- Filtered index over dead-letters whose fault has not been delivered, for fault redelivery.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_workqueue_channel_pendingfault'
      AND object_id = OBJECT_ID(N'[dbo].[workqueue]')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_workqueue_channel_pendingfault]
    ON [dbo].[workqueue] ([channel] ASC, [failedat] ASC)
    WHERE ([failedat] IS NOT NULL AND [faultdispatchedat] IS NULL)
END
