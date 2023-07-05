SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE PROCEDURE [dbo].[WorkQueue_Dequeue]
    (
    @Channel nvarchar(50),
    @Offset int
)
AS
BEGIN
   
    DELETE TOP(1) FROM dbo.WorkQueue
    OUTPUT deleted.Payload
    WHERE Id = (
    SELECT Id
    FROM WorkQueue WITH (ROWLOCK, UPDLOCK, READPAST)
        WHERE Channel = @channel
    ORDER BY Id
    OFFSET @offset ROWS
    FETCH NEXT 1 ROWS ONLY);

END

GO
