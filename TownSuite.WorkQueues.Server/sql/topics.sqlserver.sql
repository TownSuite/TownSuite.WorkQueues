-- Topic subscriptions and per-subscription message copies for TownSuite.WorkQueues.Server.
-- Idempotent and runs as one batch; "dbo" is replaced with the configured schema.

IF SCHEMA_ID(N'dbo') IS NULL EXEC(N'CREATE SCHEMA [dbo]');

IF OBJECT_ID(N'[dbo].[topicsubscription]', N'U') IS NULL
    CREATE TABLE [dbo].[topicsubscription] (
        [topic]        NVARCHAR(200) NOT NULL,
        [subscriberid] NVARCHAR(200) NOT NULL,
        [createdat]    DATETIME2     NOT NULL CONSTRAINT [DF_topicsubscription_createdat] DEFAULT (SYSUTCDATETIME()),
        [expiresat]    DATETIME2     NOT NULL,
        CONSTRAINT [PK_topicsubscription] PRIMARY KEY CLUSTERED ([topic], [subscriberid])
    );

-- One row per copy. availableat is when the copy may next be claimed: publish time, then the end
-- of each claim's lease, or the retry time after a negative acknowledgement.
IF OBJECT_ID(N'[dbo].[topicdelivery]', N'U') IS NULL
    CREATE TABLE [dbo].[topicdelivery] (
        [id]           BIGINT           IDENTITY(1,1) NOT NULL,
        [messageid]    UNIQUEIDENTIFIER NOT NULL,
        [topic]        NVARCHAR(200)    NOT NULL,
        [subscriberid] NVARCHAR(200)    NOT NULL,
        [payload]      NVARCHAR(MAX)    NOT NULL,
        [tracecontext] NVARCHAR(1000)   NULL,
        [publishedat]  DATETIME2        NOT NULL,
        [attempts]     INT              NOT NULL CONSTRAINT [DF_topicdelivery_attempts] DEFAULT (0),
        [availableat]  DATETIME2        NOT NULL,
        [locktoken]    UNIQUEIDENTIFIER NULL,
        [ackedat]      DATETIME2        NULL,
        [failedat]     DATETIME2        NULL,
        [lasterror]    NVARCHAR(MAX)    NULL,
        CONSTRAINT [PK_topicdelivery] PRIMARY KEY CLUSTERED ([id])
    );

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_topicdelivery_pending' AND object_id = OBJECT_ID(N'[dbo].[topicdelivery]'))
    CREATE NONCLUSTERED INDEX [IX_topicdelivery_pending]
    ON [dbo].[topicdelivery] ([topic], [subscriberid], [id])
    INCLUDE ([availableat], [attempts])
    WHERE ([ackedat] IS NULL AND [failedat] IS NULL);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_topicdelivery_acked' AND object_id = OBJECT_ID(N'[dbo].[topicdelivery]'))
    CREATE NONCLUSTERED INDEX [IX_topicdelivery_acked]
    ON [dbo].[topicdelivery] ([ackedat])
    WHERE ([ackedat] IS NOT NULL);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_topicdelivery_failed' AND object_id = OBJECT_ID(N'[dbo].[topicdelivery]'))
    CREATE NONCLUSTERED INDEX [IX_topicdelivery_failed]
    ON [dbo].[topicdelivery] ([failedat])
    WHERE ([failedat] IS NOT NULL);
