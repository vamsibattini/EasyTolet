-- Select the AdminService schema in Workbench. Roles already exists: do not recreate it.
-- Required BEFORE running the Roles APIs: missing outbox storage causes the entire role mutation to roll back.
CREATE TABLE IF NOT EXISTS AdminOutboxMessages
(
    Id BIGINT NOT NULL AUTO_INCREMENT,
    EventId VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    OccurredAtUtc DATETIME(6) NOT NULL,
    Payload LONGTEXT NOT NULL,
    PublishedAtUtc DATETIME(6) NULL,
    PRIMARY KEY (Id),
    UNIQUE KEY IX_AdminOutboxMessages_EventId (EventId),
    KEY IX_AdminOutboxMessages_PublishedAtUtc_Id (PublishedAtUtc, Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
