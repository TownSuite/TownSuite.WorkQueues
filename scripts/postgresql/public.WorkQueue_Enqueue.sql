CREATE OR REPLACE PROCEDURE public.workqueue_enqueue(
    p_channel VARCHAR(50),
    p_payload TEXT
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO public.workqueue (channel, payload)
    VALUES (p_channel, p_payload);
END;
$$;
