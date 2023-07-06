SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE PROCEDURE [dbo].[WorkQueue_Dequeue]
    (
    @p_channel nvarchar(50),
    @p_offset int,
    @p_payload nvarchar(MAX) OUTPUT
)
AS
BEGIN
   
    DELETE TOP(1) FROM dbo.workqueue
    OUTPUT deleted.payload as p_payload
    WHERE id = (
    SELECT id
    FROM workqueue WITH (ROWLOCK, UPDLOCK, READPAST)
        WHERE channel = @p_channel
    ORDER BY id
    OFFSET @p_offset ROWS
    FETCH NEXT 1 ROWS ONLY);

END

GO
