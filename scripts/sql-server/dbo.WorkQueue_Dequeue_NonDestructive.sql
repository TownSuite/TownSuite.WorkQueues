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

    DECLARE @the_payload nvarchar(max);
    UPDATE TOP(1) dbo.workqueue
    SET timeprocessedutc = GETUTCDATE(), @the_payload=payload
    WHERE id = (
    SELECT id
    FROM workqueue WITH (ROWLOCK, UPDLOCK, READPAST)
        WHERE channel = @p_channel and timeprocessedutc is null
    ORDER BY id
    OFFSET @p_offset ROWS
    FETCH NEXT 1 ROWS ONLY);

    SELECT @p_payload = @the_payload;

END

GO
