CREATE OR REPLACE PROCEDURE public.workqueue_dequeue(
    p_channel VARCHAR(50),
    p_offset INT,
    OUT p_payload TEXT
)
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM public.workqueue
    WHERE id IN (
        WITH cte AS (
            SELECT id, payload
            FROM public.workqueue
            WHERE channel = p_channel
            ORDER BY id
            OFFSET p_offset ROWS
            FETCH NEXT 1 ROWS ONLY
            FOR UPDATE SKIP LOCKED
        )
        SELECT id FROM cte
    )
    RETURNING payload INTO p_payload;
END;
$$;