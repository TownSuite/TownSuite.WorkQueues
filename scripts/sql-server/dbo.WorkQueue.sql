SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[workqueue](
	[id] [int] IDENTITY(1,1) NOT NULL,
	[timecreatedutc] [datetime] NOT NULL,
	[channel] [nvarchar](50) NOT NULL,
	[payload] [nvarchar](max) NOT NULL,
 CONSTRAINT [PK_WorkQueue] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
ALTER TABLE [dbo].[workqueue] ADD  CONSTRAINT [DEFAULT_WorkQueue_TimeCreatedUtc]  DEFAULT (getutcdate()) FOR [timecreatedutc]
GO
