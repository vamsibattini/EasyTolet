-- NotificationService-owned database only. Apply before starting its RabbitMQ consumer/worker.
CREATE TABLE NotificationInbox (
    Id char(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    Source varchar(80) NOT NULL,
    Channel varchar(10) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PayloadHash char(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ProtectedPayload mediumtext DEFAULT NULL,
    CreatedAtUtc datetime(6) NOT NULL,
    ExpiresAtUtc datetime(6) NOT NULL,
    NextAttemptAtUtc datetime(6) NOT NULL,
    Status varchar(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Attempts int NOT NULL DEFAULT 0,
    LeaseId char(36) CHARACTER SET ascii COLLATE ascii_bin DEFAULT NULL,
    DeliveredAtUtc datetime(6) DEFAULT NULL,
    LastErrorCode varchar(40) DEFAULT NULL,
    KEY IX_NotificationInbox_Pending (Status, NextAttemptAtUtc),
    KEY IX_NotificationInbox_Expiry (ExpiresAtUtc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
