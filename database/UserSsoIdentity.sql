-- Apply once to an existing Users table before enabling SSO. MySQL DDL commits implicitly;
-- run during deployment, never inside a registration transaction.
-- Review existing duplicate provider/subject pairs first. This migration intentionally fails
-- rather than deleting/merging accounts. Keep the original nonunique lookup index if present.
-- Canonical provider values written by the application: Google, Facebook, Microsoft.
ALTER TABLE Users
    MODIFY SSOProvider varchar(30) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL,
    MODIFY SSOProviderUserId varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL,
    ADD UNIQUE KEY UK_Users_SsoIdentity (SSOProvider, SSOProviderUserId);
