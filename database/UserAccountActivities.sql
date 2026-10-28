-- Apply after Users.sql and UserLoginSessions.sql, outside application transactions.
CREATE TABLE UserAccountChallenges (
    TokenHash char(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    UserId bigint NOT NULL,
    Purpose varchar(10) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Destination varchar(320) COLLATE utf8mb4_bin NOT NULL,
    OriginalDestination varchar(320) COLLATE utf8mb4_bin NOT NULL,
    CredentialStamp char(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CreatedAtUtc datetime(6) NOT NULL,
    ExpiresAtUtc datetime(6) NOT NULL,
    ConsumedAtUtc datetime(6) DEFAULT NULL,
    KEY IX_UserAccountChallenges_UserPurpose (UserId, Purpose, CreatedAtUtc),
    CONSTRAINT FK_UserAccountChallenges_User FOREIGN KEY (UserId) REFERENCES Users(Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE UserVerifiedContacts (
    UserId bigint NOT NULL,
    Channel varchar(10) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Destination varchar(320) COLLATE utf8mb4_bin NOT NULL,
    VerifiedAtUtc datetime(6) NOT NULL,
    PRIMARY KEY (UserId, Channel),
    CONSTRAINT FK_UserVerifiedContacts_User FOREIGN KEY (UserId) REFERENCES Users(Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE TABLE UserAccountMessages (
    Id char(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    TokenHash char(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ProtectedPayload text DEFAULT NULL,
    Attempts int NOT NULL DEFAULT 0,
    NextAttemptAtUtc datetime(6) NOT NULL,
    ExpiresAtUtc datetime(6) NOT NULL,
    DeliveredAtUtc datetime(6) DEFAULT NULL,
    LeaseId char(36) CHARACTER SET ascii COLLATE ascii_bin DEFAULT NULL,
    KEY IX_UserAccountMessages_Pending (DeliveredAtUtc, NextAttemptAtUtc),
    CONSTRAINT FK_UserAccountMessages_Challenge FOREIGN KEY (TokenHash) REFERENCES UserAccountChallenges(TokenHash) ON DELETE CASCADE
) ENGINE=InnoDB;
