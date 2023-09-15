CREATE TABLE public.workqueue (
    id SERIAL PRIMARY KEY,
    timecreatedutc TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    channel VARCHAR(50) NOT NULL,
    payload TEXT NOT NULL,
    timeprocessedutc TIMESTAMP NULL
);
