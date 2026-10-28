-- UserService-owned InnoDB tables. Apply during deployment, never inside a login transaction.
CREATE TABLE UserLoginSessions (
    Id char(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    UserId bigint NOT NULL,
    CreatedAtUtc datetime(6) NOT NULL,
    LastActivityAtUtc datetime(6) NOT NULL,
    ExpiresAtUtc datetime(6) NOT NULL,
    RevokedAtUtc datetime(6) DEFAULT NULL,
    CredentialStamp char(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    KEY IX_UserLoginSessions_UserId (UserId),
    KEY IX_UserLoginSessions_ExpiresAtUtc (ExpiresAtUtc),
    CONSTRAINT FK_UserLoginSessions_User FOREIGN KEY (UserId) REFERENCES Users(Id)
) ENGINE=InnoDB;
CREATE TABLE UserRefreshTokens (
    TokenHash char(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    SessionId char(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CreatedAtUtc datetime(6) NOT NULL,
    ConsumedAtUtc datetime(6) DEFAULT NULL,
    KEY IX_UserRefreshTokens_SessionId (SessionId),
    CONSTRAINT FK_UserRefreshTokens_Session FOREIGN KEY (SessionId) REFERENCES UserLoginSessions(Id) ON DELETE CASCADE
) ENGINE=InnoDB;
-- Only trusted administration/provisioning may write these grants. Public registration never writes them.
CREATE TABLE UserRoleGrants (
    UserId bigint NOT NULL PRIMARY KEY,
    RoleId int NOT NULL,
    ApprovedBy bigint NOT NULL,
    ApprovedAtUtc datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT FK_UserRoleGrants_User FOREIGN KEY (UserId) REFERENCES Users(Id),
    CONSTRAINT FK_UserRoleGrants_Role FOREIGN KEY (RoleId) REFERENCES Roles(RoleId)
) ENGINE=InnoDB;
CREATE TABLE RolePermissions (
    RoleId int NOT NULL,
    Permission varchar(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PRIMARY KEY (RoleId, Permission),
    CONSTRAINT FK_RolePermissions_Role FOREIGN KEY (RoleId) REFERENCES Roles(RoleId)
) ENGINE=InnoDB;
