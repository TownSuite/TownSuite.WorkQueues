CREATE OR REPLACE PROCEDURE public.workqueue_dequeue_nondestructive(
    p_channel VARCHAR(500),
    p_offset INT,
    OUT p_payload TEXT
)
LANGUAGE plpgsql
AS $$
BEGIN
    update public.workqueue set timeprocessedutc = (now() AT TIME ZONE 'utc')
    WHERE id IN (
        WITH cte AS (
            SELECT id, payload
            FROM public.workqueue
            WHERE channel = p_channel and timeprocessedutc is null
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