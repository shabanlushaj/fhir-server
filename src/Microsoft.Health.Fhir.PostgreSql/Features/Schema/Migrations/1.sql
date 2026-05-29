-- PostgreSQL FHIR Server Schema V1
-- Creates all tables required for FHIR resource storage and search-parameter indexing.

-- ============================================================
-- Resource Type lookup table
-- ============================================================
CREATE TABLE IF NOT EXISTS resource_type
(
    resource_type_id    SMALLSERIAL     PRIMARY KEY,
    name                VARCHAR(50)     NOT NULL,
    CONSTRAINT uq_resource_type_name UNIQUE (name)
);

-- ============================================================
-- Search Parameter lookup table
-- ============================================================
CREATE TABLE IF NOT EXISTS search_param
(
    search_param_id     SMALLSERIAL     PRIMARY KEY,
    uri                 VARCHAR(128)    NOT NULL,
    status              VARCHAR(20)     NOT NULL DEFAULT 'Enabled',
    last_updated        TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    is_partial_indexed  BOOLEAN         NOT NULL DEFAULT false,
    CONSTRAINT uq_search_param_uri UNIQUE (uri)
);

-- ============================================================
-- System URI lookup (used for token search params)
-- ============================================================
CREATE TABLE IF NOT EXISTS system
(
    system_id           SERIAL          PRIMARY KEY,
    value               VARCHAR(256)    NOT NULL,
    CONSTRAINT uq_system_value UNIQUE (value)
);

-- ============================================================
-- Quantity Code lookup
-- ============================================================
CREATE TABLE IF NOT EXISTS quantity_code
(
    quantity_code_id    SERIAL          PRIMARY KEY,
    value               VARCHAR(256)    NOT NULL,
    CONSTRAINT uq_quantity_code_value UNIQUE (value)
);

-- ============================================================
-- Claim Type lookup
-- ============================================================
CREATE TABLE IF NOT EXISTS claim_type
(
    claim_type_id       SMALLSERIAL     PRIMARY KEY,
    name                VARCHAR(128)    NOT NULL,
    CONSTRAINT uq_claim_type_name UNIQUE (name)
);

-- ============================================================
-- Core RESOURCE table
-- Raw resource stored as gzip-compressed bytes.
-- Partitioned by resource_type_id (list partitioning).
-- ============================================================
CREATE TABLE IF NOT EXISTS resource
(
    resource_type_id            SMALLINT        NOT NULL,
    resource_id                 VARCHAR(64)     NOT NULL,
    version                     INTEGER         NOT NULL,
    is_history                  BOOLEAN         NOT NULL DEFAULT false,
    resource_surrogate_id       BIGINT          NOT NULL,
    is_deleted                  BOOLEAN         NOT NULL DEFAULT false,
    request_method              VARCHAR(10)     NULL,
    raw_resource                BYTEA           NOT NULL,
    is_raw_resource_meta_set    BOOLEAN         NOT NULL DEFAULT false,
    search_param_hash           VARCHAR(64)     NULL,
    transaction_id              BIGINT          NULL,
    history_transaction_id      BIGINT          NULL,
    CONSTRAINT pk_resource PRIMARY KEY (resource_type_id, resource_surrogate_id)
) PARTITION BY LIST (resource_type_id);

CREATE INDEX IF NOT EXISTS ix_resource_resource_type_id_resource_id_version
    ON resource (resource_type_id, resource_id, version);

CREATE UNIQUE INDEX IF NOT EXISTS ix_resource_resource_type_id_resource_id_is_history
    ON resource (resource_type_id, resource_id)
    WHERE is_history = false;

CREATE INDEX IF NOT EXISTS ix_resource_resource_type_id_resource_surrogate_id_active
    ON resource (resource_type_id, resource_surrogate_id)
    WHERE is_history = false AND is_deleted = false;

-- ============================================================
-- Resource Write Claims (audit / provenance)
-- ============================================================
CREATE TABLE IF NOT EXISTS resource_write_claim
(
    resource_surrogate_id   BIGINT          NOT NULL,
    claim_type_id           SMALLINT        NOT NULL,
    claim_value             VARCHAR(128)    NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_resource_write_claim_resource_surrogate_id
    ON resource_write_claim (resource_surrogate_id);

-- ============================================================
-- Compartment assignment table
-- ============================================================
CREATE TABLE IF NOT EXISTS compartment_assignment
(
    resource_type_id        SMALLINT    NOT NULL,
    resource_surrogate_id   BIGINT      NOT NULL,
    compartment_type_id     SMALLINT    NOT NULL,
    reference_resource_id   VARCHAR(64) NOT NULL,
    is_history              BOOLEAN     NOT NULL DEFAULT false
);

CREATE INDEX IF NOT EXISTS ix_compartment_assignment_lookup
    ON compartment_assignment (compartment_type_id, reference_resource_id, resource_type_id)
    WHERE is_history = false;

-- ============================================================
-- Reference Search Param
-- ============================================================
CREATE TABLE IF NOT EXISTS reference_search_param
(
    resource_type_id            SMALLINT        NOT NULL,
    resource_surrogate_id       BIGINT          NOT NULL,
    search_param_id             SMALLINT        NOT NULL,
    base_uri                    VARCHAR(128)    NULL,
    reference_resource_type_id  SMALLINT        NULL,
    reference_resource_id       VARCHAR(64)     NOT NULL,
    reference_resource_version  INT             NULL,
    is_history                  BOOLEAN         NOT NULL DEFAULT false
);

CREATE INDEX IF NOT EXISTS ix_reference_search_param_lookup
    ON reference_search_param (search_param_id, reference_resource_id, resource_type_id)
    WHERE is_history = false;

-- ============================================================
-- Token Search Param
-- ============================================================
CREATE TABLE IF NOT EXISTS token_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    system_id               INTEGER         NULL,
    code                    VARCHAR(256)    NOT NULL,
    code_overflow           TEXT            NULL,
    is_history              BOOLEAN         NOT NULL DEFAULT false
) PARTITION BY LIST (resource_type_id);

CREATE INDEX IF NOT EXISTS ix_token_search_param_lookup
    ON token_search_param (search_param_id, code)
    INCLUDE (system_id)
    WHERE is_history = false;

-- ============================================================
-- String Search Param
-- ============================================================
CREATE TABLE IF NOT EXISTS string_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    text                    VARCHAR(256)    NOT NULL,
    text_overflow           TEXT            NULL,
    is_min                  BOOLEAN         NOT NULL DEFAULT false,
    is_max                  BOOLEAN         NOT NULL DEFAULT false,
    is_history              BOOLEAN         NOT NULL DEFAULT false
) PARTITION BY LIST (resource_type_id);

CREATE INDEX IF NOT EXISTS ix_string_search_param_lookup
    ON string_search_param (search_param_id, text)
    WHERE is_history = false;

-- ============================================================
-- Uri Search Param
-- ============================================================
CREATE TABLE IF NOT EXISTS uri_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    uri                     VARCHAR(256)    NOT NULL,
    is_history              BOOLEAN         NOT NULL DEFAULT false
);

CREATE INDEX IF NOT EXISTS ix_uri_search_param_lookup
    ON uri_search_param (search_param_id, uri)
    WHERE is_history = false;

-- ============================================================
-- Number Search Param
-- ============================================================
CREATE TABLE IF NOT EXISTS number_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    single_value            NUMERIC(36,18)  NULL,
    low_value               NUMERIC(36,18)  NOT NULL,
    high_value              NUMERIC(36,18)  NOT NULL,
    is_history              BOOLEAN         NOT NULL DEFAULT false
);

CREATE INDEX IF NOT EXISTS ix_number_search_param_lookup
    ON number_search_param (search_param_id, single_value)
    WHERE is_history = false AND single_value IS NOT NULL;

-- ============================================================
-- Quantity Search Param
-- ============================================================
CREATE TABLE IF NOT EXISTS quantity_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    system_id               INTEGER         NULL,
    quantity_code_id        INTEGER         NULL,
    single_value            NUMERIC(36,18)  NULL,
    low_value               NUMERIC(36,18)  NOT NULL,
    high_value              NUMERIC(36,18)  NOT NULL,
    is_history              BOOLEAN         NOT NULL DEFAULT false
);

CREATE INDEX IF NOT EXISTS ix_quantity_search_param_lookup
    ON quantity_search_param (search_param_id, quantity_code_id, single_value)
    WHERE is_history = false AND single_value IS NOT NULL;

-- ============================================================
-- DateTime Search Param
-- ============================================================
CREATE TABLE IF NOT EXISTS date_time_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    start_date_time         TIMESTAMPTZ     NOT NULL,
    end_date_time           TIMESTAMPTZ     NOT NULL,
    is_min                  BOOLEAN         NOT NULL DEFAULT false,
    is_max                  BOOLEAN         NOT NULL DEFAULT false,
    is_history              BOOLEAN         NOT NULL DEFAULT false
);

CREATE INDEX IF NOT EXISTS ix_date_time_search_param_lookup
    ON date_time_search_param (search_param_id, start_date_time, end_date_time)
    WHERE is_history = false;

-- ============================================================
-- Token-Date composite search param
-- ============================================================
CREATE TABLE IF NOT EXISTS token_date_time_composite_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    system_id1              INTEGER         NULL,
    code1                   VARCHAR(256)    NOT NULL,
    start_date_time2        TIMESTAMPTZ     NOT NULL,
    end_date_time2          TIMESTAMPTZ     NOT NULL,
    is_long_code1           BOOLEAN         NOT NULL DEFAULT false,
    is_history              BOOLEAN         NOT NULL DEFAULT false
);

-- ============================================================
-- Token-Quantity composite search param
-- ============================================================
CREATE TABLE IF NOT EXISTS token_quantity_composite_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    system_id1              INTEGER         NULL,
    code1                   VARCHAR(256)    NOT NULL,
    system_id2              INTEGER         NULL,
    quantity_code_id2       INTEGER         NULL,
    single_value2           NUMERIC(36,18)  NULL,
    low_value2              NUMERIC(36,18)  NOT NULL,
    high_value2             NUMERIC(36,18)  NOT NULL,
    is_history              BOOLEAN         NOT NULL DEFAULT false
);

-- ============================================================
-- Token-String composite search param
-- ============================================================
CREATE TABLE IF NOT EXISTS token_string_composite_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    system_id1              INTEGER         NULL,
    code1                   VARCHAR(256)    NOT NULL,
    text2                   VARCHAR(256)    NOT NULL,
    text_overflow2          TEXT            NULL,
    is_long_code1           BOOLEAN         NOT NULL DEFAULT false,
    is_history              BOOLEAN         NOT NULL DEFAULT false
);

-- ============================================================
-- Token-Token composite search param
-- ============================================================
CREATE TABLE IF NOT EXISTS token_token_composite_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    system_id1              INTEGER         NULL,
    code1                   VARCHAR(256)    NOT NULL,
    system_id2              INTEGER         NULL,
    code2                   VARCHAR(256)    NOT NULL,
    is_history              BOOLEAN         NOT NULL DEFAULT false
);

-- ============================================================
-- Token-Number-Number composite search param
-- ============================================================
CREATE TABLE IF NOT EXISTS token_number_number_composite_search_param
(
    resource_type_id        SMALLINT        NOT NULL,
    resource_surrogate_id   BIGINT          NOT NULL,
    search_param_id         SMALLINT        NOT NULL,
    system_id1              INTEGER         NULL,
    code1                   VARCHAR(256)    NOT NULL,
    single_value2           NUMERIC(36,18)  NULL,
    low_value2              NUMERIC(36,18)  NOT NULL,
    high_value2             NUMERIC(36,18)  NOT NULL,
    single_value3           NUMERIC(36,18)  NULL,
    low_value3              NUMERIC(36,18)  NOT NULL,
    high_value3             NUMERIC(36,18)  NOT NULL,
    has_range               BOOLEAN         NOT NULL DEFAULT false,
    is_history              BOOLEAN         NOT NULL DEFAULT false
);

-- ============================================================
-- Schema version tracking
-- ============================================================
CREATE TABLE IF NOT EXISTS schema_version
(
    version     INTEGER     PRIMARY KEY,
    status      VARCHAR(10) NOT NULL
);

INSERT INTO schema_version (version, status) VALUES (1, 'complete') ON CONFLICT DO NOTHING;
