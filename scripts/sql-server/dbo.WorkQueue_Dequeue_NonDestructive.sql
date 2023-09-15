SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE PROCEDURE [dbo].[WorkQueue_Dequeue_NonDestructive]
    (
    @p_channel nvarchar(50),
    @p_offset int,
    @p_payload nvarchar(MAX) OUTPUT
)
AS
BEGIN

    UPDATE TOP(1) dbo.workqueue
    SET timeprocessedutc = GETUTCDATE()
    WHERE id = 
    
    (select top 1
        id
    from (
    SELECT id
        FROM workqueue WITH (ROWLOCK, UPDLOCK, READPAST)
        WHERE channel = @p_channel and timeprocessedutc is null
        ORDER BY id
    OFFSET @p_offset ROWS) tbl);

END

GO
