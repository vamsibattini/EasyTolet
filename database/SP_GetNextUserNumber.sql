-- Requires an InnoDB UserIdSequence table with exactly one row for SequenceId = 1.
-- Initialize LastNumber to the last issued number before enabling registration.
-- Run within the caller's transaction; do not add START TRANSACTION or COMMIT here.
DELIMITER $$
CREATE PROCEDURE `SP_GetNextUserNumber`(OUT p_NextNumber BIGINT)
BEGIN
    UPDATE UserIdSequence
    SET LastNumber = LAST_INSERT_ID(LastNumber + 1)
    WHERE SequenceId = 1;

    SET p_NextNumber = LAST_INSERT_ID();
END$$
DELIMITER ;
