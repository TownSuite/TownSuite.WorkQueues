CREATE OR REPLACE FUNCTION public.WorkQueue_Enqueue(
    p_Channel VARCHAR(50),
    p_Payload TEXT
)
RETURNS VOID AS $$
BEGIN
    INSERT INTO public.WorkQueue (Channel, Payload)
    VALUES (p_Channel, p_Payload);
END;
$$ LANGUAGE plpgsql;
