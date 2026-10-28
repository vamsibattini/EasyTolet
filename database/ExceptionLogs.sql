-- MySQL 8+. Select the owning service's schema in Workbench before executing.
-- Run once in each service database and the gateway's logging database.
-- No USE statement: this script does not select or create a database for you.
CREATE TABLE IF NOT EXISTS ExceptionLogs
(
    Id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    OccurredAtUtc DATETIME(6) NOT NULL,
    Service VARCHAR(150) NOT NULL,
    Environment VARCHAR(50) NOT NULL,
    Instance VARCHAR(100) NOT NULL,
    CorrelationId VARCHAR(128) NOT NULL,
    TraceId VARCHAR(128) NOT NULL,
    Method VARCHAR(16) NOT NULL,
    Path VARCHAR(2048) NOT NULL,
    Controller VARCHAR(512) NULL,
    Action VARCHAR(256) NULL,
    Repository LONGTEXT NULL,
    StatusCode SMALLINT UNSIGNED NOT NULL,
    ResponseStarted BOOLEAN NOT NULL,
    ExceptionType VARCHAR(512) NOT NULL,
    Message LONGTEXT NOT NULL,
    ExceptionDetails LONGTEXT NOT NULL,
    PRIMARY KEY (Id),
    INDEX IX_ExceptionLogs_OccurredAtUtc (OccurredAtUtc),
    INDEX IX_ExceptionLogs_CorrelationId (CorrelationId),
    INDEX IX_ExceptionLogs_TraceId (TraceId),
    INDEX IX_ExceptionLogs_Service_Time (Service, OccurredAtUtc)
) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

-- Optional query after logging is configured:
-- SELECT Id, OccurredAtUtc, Service, Controller, Action, Message, ExceptionDetails
-- FROM ExceptionLogs ORDER BY OccurredAtUtc DESC LIMIT 100;
-- Give the logging account INSERT on this table; use an administrator for DDL/retention.
