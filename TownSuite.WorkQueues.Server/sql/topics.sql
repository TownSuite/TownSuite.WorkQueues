-- Topic subscriptions and per-subscription message copies for TownSuite.WorkQueues.Server.
-- Idempotent; "public." is replaced with the configured schema.

CREATE TABLE IF NOT EXISTS public.topicsubscription (
    topic        VARCHAR(200) NOT NULL,
    subscriberid VARCHAR(200) NOT NULL,
    createdat    TIMESTAMP    NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
    expiresat    TIMESTAMP    NOT NULL,
    PRIMARY KEY (topic, subscriberid)
);

-- One row per copy. availableat is when the copy may next be claimed: publish time, then the end
-- of each claim's lease, or the retry time after a negative acknowledgement.
CREATE TABLE IF NOT EXISTS public.topicdelivery (
    id           BIGSERIAL    PRIMARY KEY,
    messageid    UUID         NOT NULL,
    topic        VARCHAR(200) NOT NULL,
    subscriberid VARCHAR(200) NOT NULL,
    payload      TEXT         NOT NULL,
    tracecontext TEXT         NULL,
    publishedat  TIMESTAMP    NOT NULL,
    attempts     INT          NOT NULL DEFAULT 0,
    availableat  TIMESTAMP    NOT NULL,
    locktoken    UUID         NULL,
    ackedat      TIMESTAMP    NULL,
    failedat     TIMESTAMP    NULL,
    lasterror    TEXT         NULL
);

CREATE INDEX IF NOT EXISTS ix_topicdelivery_pending
    ON public.topicdelivery (topic, subscriberid, id)
    WHERE ackedat IS NULL AND failedat IS NULL;

CREATE INDEX IF NOT EXISTS ix_topicdelivery_acked
    ON public.topicdelivery (ackedat)
    WHERE ackedat IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_topicdelivery_failed
    ON public.topicdelivery (failedat)
    WHERE failedat IS NOT NULL;
