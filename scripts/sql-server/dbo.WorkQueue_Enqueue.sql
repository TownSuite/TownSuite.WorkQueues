SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE PROCEDURE [dbo].[WorkQueue_Enqueue]
    (
    @p_channel nvarchar(50),
    @p_payload nvarchar(max)
)
AS
BEGIN

    INSERT INTO dbo.WorkQueue ([Channel],[Payload]) VALUES(@p_channel, @p_payload);

END

GO
