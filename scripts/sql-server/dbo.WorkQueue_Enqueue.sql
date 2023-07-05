SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE PROCEDURE [dbo].[WorkQueue_Enqueue]
    (
    @Channel nvarchar(50),
    @Payload nvarchar(max)
)
AS
BEGIN

    INSERT INTO dbo.WorkQueue ([Channel],[Payload]) VALUES(@Channel, @Payload);

END

GO
