-- Test fixture matching the user-provided existing schema; do not run against production.
CREATE TABLE IF NOT EXISTS Roles (
 RoleId INT NOT NULL AUTO_INCREMENT,
 RoleCode VARCHAR(10) NOT NULL,
 RoleName VARCHAR(100) NOT NULL,
 ParentRoleId INT NULL,
 Description VARCHAR(500) NULL,
 IsActive TINYINT(1) NOT NULL DEFAULT 1,
 CreatedDate DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 UpdatedDate DATETIME(6) NULL,
 CreatedBy INT NULL,
 UpdatedBy INT NULL,
 PRIMARY KEY (RoleId),
 UNIQUE KEY UK_Roles_RoleCode (RoleCode),
 KEY FK_Roles_ParentRole (ParentRoleId),
 CONSTRAINT FK_Roles_ParentRole FOREIGN KEY (ParentRoleId) REFERENCES Roles(RoleId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
