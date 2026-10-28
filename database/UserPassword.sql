-- Existing tables only. New database/Users.sql already includes this column.
-- Never store a plaintext password or a reversible encrypted password.
ALTER TABLE Users ADD COLUMN PasswordHash varchar(512) DEFAULT NULL AFTER EmailId;
-- Existing and SSO-only users keep NULL until they set a password through a trusted flow.
-- Deploy the updated SP_User (new final p_PasswordHash parameter) and application together.
