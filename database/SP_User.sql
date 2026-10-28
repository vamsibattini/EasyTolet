-- Requires Users, Roles, SP_GetNextUserNumber and FN_FormatUserId in the UserService database.
-- Requires Users.PasswordHash (apply UserPassword.sql first for existing tables).
-- Run in the UserService database with the mysql client or a script-capable SQL editor.
-- Replaces the existing routine; deploy with the application that supplies all 23 parameters.
-- p_PasswordHash is an application-generated salted PBKDF2 hash, never plaintext.
-- Passwords are verified in the application, not decrypted or returned by profile reads.
DELIMITER $$
DROP PROCEDURE IF EXISTS `SP_User`$$
CREATE PROCEDURE `SP_User`(
    IN p_Action CHAR(1),

    IN p_Id BIGINT,
    IN p_UserId VARCHAR(20),

    IN p_RoleId INT,

    IN p_FirstName VARCHAR(150),
    IN p_LastName VARCHAR(150),

    IN p_Address1 VARCHAR(500),
    IN p_Address2 VARCHAR(500),
    IN p_Address3 VARCHAR(500),

    IN p_Country VARCHAR(100),
    IN p_State VARCHAR(150),
    IN p_City VARCHAR(150),
    IN p_PinNumber VARCHAR(20),

    IN p_MobileNumber VARCHAR(30),
    IN p_AlternateMobileNumber VARCHAR(30),

    IN p_EmailId VARCHAR(320),

    IN p_SSOProvider VARCHAR(30),
    IN p_SSOProviderUserId VARCHAR(255),
    IN p_SSOEmailId VARCHAR(320),
    IN p_SSOProfileData JSON,

    IN p_IsActive BOOLEAN,

    IN p_ActionBy BIGINT,
    IN p_PasswordHash VARCHAR(512)
)
BEGIN

    DECLARE v_NextNumber BIGINT;
    DECLARE v_UserId VARCHAR(20);

    /* =========================
       INSERT
       ========================= */

    IF p_Action = 'I' THEN

        CALL SP_GetNextUserNumber(v_NextNumber);

        SET v_UserId =
            FN_FormatUserId(v_NextNumber);

        INSERT INTO Users
        (
            UserId,
            RoleId,

            FirstName,
            LastName,

            Address1,
            Address2,
            Address3,

            Country,
            State,
            City,
            PinNumber,

            MobileNumber,
            AlternateMobileNumber,

            EmailId,
            PasswordHash,

            SSOProvider,
            SSOProviderUserId,
            SSOEmailId,
            SSOProfileData,

            CreatedDate,
            CreatedBy,
            IsActive
        )
        VALUES
        (
            v_UserId,
            p_RoleId,

            p_FirstName,
            p_LastName,

            p_Address1,
            p_Address2,
            p_Address3,

            p_Country,
            p_State,
            p_City,
            p_PinNumber,

            p_MobileNumber,
            p_AlternateMobileNumber,

            p_EmailId,
            p_PasswordHash,

            p_SSOProvider,
            p_SSOProviderUserId,
            p_SSOEmailId,
            p_SSOProfileData,

            CURRENT_TIMESTAMP(6),
            p_ActionBy,
            COALESCE(p_IsActive, TRUE)
        );

        /*
          For self registration, CreatedBy can be
          updated to the newly created user's Id.
        */

        UPDATE Users
        SET CreatedBy = Id
        WHERE Id = LAST_INSERT_ID();

        SELECT
            Id,
            UserId,
            RoleId,
            FirstName,
            LastName,
            EmailId,
            MobileNumber,
            IsActive,
            CreatedDate
        FROM Users
        WHERE Id = LAST_INSERT_ID();

    /* =========================
       READ
       ========================= */

    ELSEIF p_Action = 'R' THEN

        SELECT
            u.Id,
            u.UserId,

            u.RoleId,
            r.RoleCode,
            r.RoleName,

            u.FirstName,
            u.LastName,

            u.Address1,
            u.Address2,
            u.Address3,

            u.Country,
            u.State,
            u.City,
            u.PinNumber,

            u.MobileNumber,
            u.AlternateMobileNumber,

            u.EmailId,

            u.SSOProvider,
            u.SSOProviderUserId,
            u.SSOEmailId,
            u.SSOProfileData,

            u.CreatedDate,
            u.UpdatedDate,
            u.CreatedBy,
            u.UpdatedBy,
            u.IsActive

        FROM Users u

        INNER JOIN Roles r
            ON u.RoleId = r.RoleId

        WHERE
            (
                p_Id IS NULL
                OR u.Id = p_Id
            )
            AND
            (
                p_UserId IS NULL
                OR u.UserId = p_UserId
            )
        ORDER BY u.Id;

    /* =========================
       UPDATE
       ========================= */

    ELSEIF p_Action = 'U' THEN

        UPDATE Users
        SET

            RoleId = COALESCE(p_RoleId, RoleId),

            FirstName = COALESCE(p_FirstName, FirstName),
            LastName = COALESCE(p_LastName, LastName),

            Address1 = COALESCE(p_Address1, Address1),
            Address2 = p_Address2,
            Address3 = p_Address3,

            Country = COALESCE(p_Country, Country),
            State = COALESCE(p_State, State),
            City = COALESCE(p_City, City),
            PinNumber = COALESCE(p_PinNumber, PinNumber),

            MobileNumber =
                COALESCE(p_MobileNumber, MobileNumber),

            AlternateMobileNumber =
                p_AlternateMobileNumber,

            EmailId =
                COALESCE(p_EmailId, EmailId),

            SSOProvider =
                COALESCE(p_SSOProvider, SSOProvider),

            SSOProviderUserId =
                COALESCE(
                    p_SSOProviderUserId,
                    SSOProviderUserId
                ),

            SSOEmailId =
                COALESCE(
                    p_SSOEmailId,
                    SSOEmailId
                ),

            SSOProfileData =
                COALESCE(
                    p_SSOProfileData,
                    SSOProfileData
                ),

            -- NULL means preserve the existing password (for ordinary profile updates).
            PasswordHash = COALESCE(p_PasswordHash, PasswordHash),

            UpdatedDate =
                CURRENT_TIMESTAMP(6),

            UpdatedBy =
                p_ActionBy,

            IsActive =
                COALESCE(p_IsActive, IsActive)

        WHERE
            (
                p_Id IS NOT NULL
                AND Id = p_Id
            )
            OR
            (
                p_Id IS NULL
                AND p_UserId IS NOT NULL
                AND UserId = p_UserId
            );

        SELECT ROW_COUNT() AS RowsAffected;

    /* =========================
       DELETE
       ========================= */

    ELSEIF p_Action = 'D' THEN

        /*
          Soft delete is safer for ToletHub.
          We keep the user and audit history.
        */

        UPDATE Users
        SET
            IsActive = FALSE,
            UpdatedDate = CURRENT_TIMESTAMP(6),
            UpdatedBy = p_ActionBy

        WHERE
            (
                p_Id IS NOT NULL
                AND Id = p_Id
            )
            OR
            (
                p_Id IS NULL
                AND p_UserId IS NOT NULL
                AND UserId = p_UserId
            );

        SELECT ROW_COUNT() AS RowsAffected;

    ELSE

        SIGNAL SQLSTATE '45000'
        SET MESSAGE_TEXT =
            'Invalid Action. Use I, R, U or D.';

    END IF;

END$$
DELIMITER ;
