CREATE OR REPLACE FUNCTION public.WorkQueue_Dequeue(
    p_Channel VARCHAR(50),
    p_Offset INT
)
RETURNS TEXT AS $$
DECLARE
    v_Payload TEXT;
BEGIN
    WITH cte AS (
        SELECT Id, Payload
        FROM public.WorkQueue
        WHERE Channel = p_Channel
        ORDER BY Id
        OFFSET p_Offset ROWS
        FETCH NEXT 1 ROWS ONLY
        FOR UPDATE SKIP LOCKED
    )
    DELETE FROM public.WorkQueue
    WHERE Id IN (SELECT Id FROM cte)
    RETURNING cte.Payload INTO v_Payload;

    RETURN v_Payload;
END;
$$ LANGUAGE plpgsql;
