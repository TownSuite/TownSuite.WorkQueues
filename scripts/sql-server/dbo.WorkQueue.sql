SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[workqueue](
	[id]               [int]             IDENTITY(1,1) NOT NULL,
	[messageid]        UNIQUEIDENTIFIER  NOT NULL CONSTRAINT [DEFAULT_WorkQueue_MessageId]       DEFAULT (NEWID()),
	[timecreatedutc]   [datetime]        NOT NULL CONSTRAINT [DEFAULT_WorkQueue_TimeCreatedUtc]  DEFAULT (GETUTCDATE()),
	[channel]          [nvarchar](500)   NOT NULL,
	[payload]          [nvarchar](max)   NOT NULL,
	[timeprocessedutc] [datetime]        NULL,
	[failedat]         [datetime]        NULL,
	[retrycount]       [int]             NOT NULL CONSTRAINT [DEFAULT_WorkQueue_RetryCount]      DEFAULT (0),
	[scheduledfor]     [datetime]        NULL,
	[faultdispatchedat] [datetime]       NULL,
	[lasterror]        [nvarchar](max)   NULL,
	CONSTRAINT [PK_WorkQueue] PRIMARY KEY CLUSTERED ([id] ASC)
		WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF,
		      ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [IX_WorkQueue_Channel_Unprocessed] ON [dbo].[workqueue]
(
	[channel]        ASC,
	[timecreatedutc] ASC
)
WHERE ([timeprocessedutc] IS NULL AND [failedat] IS NULL)
GO
CREATE NONCLUSTERED INDEX [IX_WorkQueue_Channel_DeadLettered] ON [dbo].[workqueue]
(
	[channel]   ASC,
	[messageid] ASC
)
WHERE ([failedat] IS NOT NULL)
GO
CREATE NONCLUSTERED INDEX [IX_WorkQueue_Channel_PendingFault] ON [dbo].[workqueue]
(
	[channel]  ASC,
	[failedat] ASC
)
WHERE ([failedat] IS NOT NULL AND [faultdispatchedat] IS NULL)
GO
